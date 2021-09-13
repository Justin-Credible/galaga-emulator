
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using JustinCredible.ZilogZ80;

namespace JustinCredible.GalagaEmu
{
    /**
     * An implementation of the Galaga game hardware for emulation; this includes the three
     * Zilog Z80 CPU instances, video & sound hardware, Namco 51XX/54XX MCUs, memory mapping,
     * interrupts, debugger, and hardware loop.
     */
    public class GalagaPCB : IMemory
    {
        // The thread on which we'll run the hardware emulation loop.
        private Thread _thread;

        // Indicates if a stop was requested via the Stop() method. Used to break out of the hardware
        // loop in the thread and stop execution.
        private bool _cancelled = false;

        // Indicates if a pause was required via the Pause() method. Used to temporarily stop stepping
        // the CPU. Can be un-paused by a call to UnPause().
        private bool _paused = false;

        // Indicates if writes should be allowed to the ROM addres space.
        public bool AllowWritableROM { get; set; } = false;

        // The ROM set the PCB should be configured for.
        public ROMSet ROMSet { get; set; } = ROMSet.GalagaNamcoRevB;

        #region Events/Delegates

        // Fired when a frame is ready to be rendered.
        public delegate void RenderEvent(RenderEventArgs e);
        public event RenderEvent OnRender;
        private RenderEventArgs _renderEventArgs = new RenderEventArgs();

        // Fired when an audio sample should be played.
        // TODO: Audio event handlers.
        // public delegate void AudioSampleEvent(AudioSampleEventArgs e);
        // public event AudioSampleEvent OnAudioSample;
        // private AudioSampleEventArgs _audioSampleEventArgs = new AudioSampleEventArgs();

        // Fired when a breakpoint is hit and the CPU is paused (only when Debug = true).
        public delegate void BreakpointHitEvent();
        public event BreakpointHitEvent OnBreakpointHitEvent;

        #endregion

        #region Hardware: Components

        // The Zilog Z80 CPUs for the Galaga hardware are all clocked at 3.072MHz.
        private const int CPU_HZ = 3072000;

        // The Namco MCUs runs at CPU_MHZ/6/2 = 1.536MHz.
        private const int NAMCO_MCU_HZ = CPU_HZ / 6 / 2;

        internal CPU _cpu; // Zilog Z80 // TODO: There should be three CPUs.
        // private VideoHardware _video; // TODO
        // private AudioHardware _audio; // TODO
        public DIPSwitches DIPSwitchState { get; set; } = new DIPSwitches();
        public Buttons ButtonState { get; set; } = new Buttons();

        // TODO: The first 64KB of address space is unique to each CPU... the rest of it is shared?
        private byte[] _memory = null;

        #endregion

        #region Hardware: Interrupts

        // TODO: Determine if this interrupt fires on each CPU.
        // The game's video hardware runs at 60hz. It generates an interrupts @ 60hz (VBLANK).To simulate
        // this, we'll calculate the number of cycles we're expecting between each of these interrupts.
        // While this is not entirely accurate, it is close enough for the game to run as expected.
        private int _cyclesPerInterrupt = CPU_HZ / 60;
        private int _cyclesSinceLastInterrupt = 0;

        // To keep the emulated CPU from running too fast, we use a stopwatch and count cycles.
        private Stopwatch _cpuStopWatch = new Stopwatch();
        private int _cycleCount = 0;

        // Holds the last data written by the CPU to ports 0, which is used by the VBLANK interrupt.
        // Interrupt mode 2 uses this as the lower 8 bits with the I register as the upper 8 bits
        // to build a jump vector. Pac-Man's game code sets this to control where the code jumps to
        // after a VBLANK interrupt. See CPU::StepMaskableInterrupt() for more details.
        private byte _port0WriteLastData = 0x00;

        #endregion

        #region Debugging Features

        private static readonly int MAX_ADDRESS_HISTORY = 50;
        private static readonly int MAX_REVERSE_STEP_HISTORY = 20;

        internal long _totalCycles = 0;
        internal long _totalOpcodes = 0;

        /**
         * Enables debugging statistics and features.
         */
        public bool Debug { get; set; } = false;

        /**
         * When Debug=true, stores the last MAX_ADDRESS_HISTORY values of the program counter.
         */
        internal List<UInt16> _addressHistory = new List<UInt16>();

        /**
         * When Debug=true, the program will break at these addresses and allow the user to perform
         * interactive debugging via the console.
         */
        public List<UInt16> BreakAtAddresses { get; set; } = new List<ushort>();

        /**
         * When Debug=true, allows for single reverse-stepping in the interactive debugging console.
         */
        public bool ReverseStepEnabled { get; set; } = false;

        /**
         * When Debug=true and ReverseStepEnabled=true, stores sufficient state of the CPU and emulator
         * to allow for stepping backwards to each state of the system.
         */
        internal List<EmulatorState> _executionHistory = new List<EmulatorState>();

        /**
         * Indicates if the thread is in a busy/lazy wait for the user to submit a command via the
         * interactive debugger.
         */
        private bool _isWaitingForInteractiveDebugger = false;

        /**
         * Indicates if we're stingle stepping through opcodes/instructions using the interactive
         * debugger when Debug=true.
         */
        private bool _singleStepping = false;

        /**
         * The disassembly annotations to be used by the interactive debugger when Debug=true. It is
         * a map of memory addresses to string annotation values.
         */
        public Dictionary<UInt16, String> Annotations { get; set; }

        #endregion

        #region Public Methods

        /**
         * Used to start execution of the CPU with the given ROM and optional emulator state.
         * The emulator's hardware loop will run on a spereate thread, and therefore, this method
         * is non-blocking.
         */
        public void Start(ROMData romData, EmulatorState state = null)
        {
            if (_thread != null)
                throw new Exception("Emulator cannot be started because it was already running.");

            if (romData == null || romData.Data == null || romData.Data.Count == 0)
                throw new Exception("romData is required.");

            if (ROMSet != ROMSet.GalagaNamcoRevB)
                throw new ArgumentException($"Unexpected ROM set: {ROMSet}");

            // TODO: Initialize each of the three CPUs.

            // The initial configuration of the CPU.
            var cpuConfig = new CPUConfig()
            {
                Registers = new CPURegisters()
                {
                    PC = 0x0000,

                    // Hardcode the stackpointer to the top of the RAM.
                    // TODO: Determine what this should be for Galaga (this was from Pac-Man).
                    SP = 0x4FEF,
                },

                // Interrupts are initially disabled, and will be enabled by the program ROM when ready.
                InterruptsEnabled = false,

                // Diagnostics is only for unit tests.
                EnableDiagnosticsMode = false,
            };

            // Initialize the CPU and subscribe to device events.
            _cpu = new CPU(cpuConfig);
            _cpu.OnDeviceRead += CPU_OnDeviceRead;
            _cpu.OnDeviceWrite += CPU_OnDeviceWrite;
            _cyclesSinceLastInterrupt = 0;

            // Fetch the ROM data; we trust the contents were validated with a CRC32 check elsewhere, but
            // since the CRC check can be bypassed, we at least need to ensure the file sizes are correct
            // since this classes' implementation of IMemory is expecting certain addreses.

            /* TODO: Read in code ROMs for each CPU and create IMemory wrapper so each CPU will be mapped correctly.
            var codeRom1 = romData.Data[ROMs.PAC_MAN_CODE_1.FileName];
            var codeRom2 = romData.Data[ROMs.PAC_MAN_CODE_2.FileName];
            var codeRom3 = romData.Data[ROMs.PAC_MAN_CODE_3.FileName];
            var codeRom4 = romData.Data[ROMs.PAC_MAN_CODE_4.FileName];

            if (codeRom1.Length != 4096 || codeRom2.Length != 4096 || codeRom3.Length != 4096 || codeRom4.Length != 4096)
                throw new Exception("All code ROMs must be exactly 4KB in size.");

            // Define our addressable memory space, which includes the game code ROMS and RAM.

            var addressableMemorySize =
                codeRom1.Length     // Code ROM 1
                + codeRom2.Length   // Code ROM 2
                + codeRom3.Length   // Code ROM 3
                + codeRom4.Length   // Code ROM 4
                + 1024              // Video RAM (tile information)
                + 1024              // Video RAM (tile palettes)
                + 2032              // RAM
                + 16;               // Sprite numbers

            _memory = new byte[addressableMemorySize];

            // Map the code ROM into the lower 16K of the memory space.
            Array.Copy(codeRom1, 0, _memory, 0, codeRom1.Length);
            Array.Copy(codeRom2, 0, _memory, codeRom1.Length, codeRom2.Length);
            Array.Copy(codeRom3, 0, _memory, codeRom1.Length + codeRom2.Length, codeRom3.Length);
            Array.Copy(codeRom4, 0, _memory, codeRom1.Length + codeRom2.Length + codeRom3.Length, codeRom4.Length);
            */

            // This class implements the IMemory interface, which the CPU needs to determine how to read and
            // write data. We set the reference to this class instance (whose implementation uses _memory).
            _cpu.Memory = this;

            // Initialize video hardware. // TODO
            // _video = new VideoHardware(romData, ROMSet);
            // _video.Initialize();

            // Initialize audio hardware. // TODO
            // _audio = new AudioHardware(romData, ROMSet);

            if (state != null)
                LoadState(state);

            _cancelled = false;
            _thread = new Thread(new ThreadStart(HardwareLoop));
            _thread.Name = "Galaga Hardware";
            _thread.Start();
        }

        /**
         * Used to stop execution of the CPU and halt the thread.
         */
        public void Stop()
        {
            if (_thread == null)
                throw new Exception("Emulator cannot be stopped because it wasn't running.");

            _cancelled = true;
        }

        /**
         * Used to temporarily stop execution of the CPU. Resume with a call to UnPause().
         */
        public void Pause()
        {
            _paused = true;
        }

        /**
         * Used to resume execution of the CPU after a call to Pause().
         */
        public void UnPause()
        {
            _paused = false;
        }

        /**
         * Used to stop CPU execution and enable single stepping through opcodes via the interactive
         * debugger (only when Debug = true).
         */
        public void Break()
        {
            if (!Debug)
                return;

            _singleStepping = true;

            if (OnBreakpointHitEvent != null)
            {
                _isWaitingForInteractiveDebugger = true;
                OnBreakpointHitEvent.Invoke();
            }
        }

        /**
         * Used to continue CPU execution (only when Debug = true).
         * If the user only wants to single step, true can be passed here.
         */
        public void Continue(bool singleStep = false)
        {
            if (!Debug || !_isWaitingForInteractiveDebugger)
                return;

            // Handle continue vs single step; if we're continuing then we want to release
            // the single step mode and continue until the next conditional breakpoint.
            if (!singleStep)
                _singleStepping = false;

            // Release the thread from busy/lazy waiting.
            _isWaitingForInteractiveDebugger = false;
        }

        /**
         * Used to reverse step backwards a single step, effectively reverting the CPU to the state
         * prior to the last opcode executing. This requires Debug=true and ReverseStepEnabled=true.
         */
        public void ReverseStep()
        {
            if (!Debug && !ReverseStepEnabled)
                throw new Exception("Debug feature: reverse stepping is not enabled.");

            var state = _executionHistory[_executionHistory.Count - 1];
            _executionHistory.RemoveAt(_executionHistory.Count - 1);

            // The first time we step backwards in the debugger, the most recent saved execution state
            // will be the same as the current state. In that case, pop again to get the last state.
            if (state.Registers.PC == _cpu.Registers.PC && _executionHistory.Count > 0)
            {
                state = _executionHistory[_executionHistory.Count - 1];
                _executionHistory.RemoveAt(_executionHistory.Count - 1);
            }

            LoadState(state);
            _cyclesSinceLastInterrupt -= state.LastCyclesExecuted.Value;
            OnBreakpointHitEvent.Invoke();
        }

        #endregion

        #region CPU Event Handlers

        /**
         * Used to handle the CPU's IN instruction; read value from given device ID.
         */
        private byte CPU_OnDeviceRead(int deviceID)
        {
            // I don't believe the Galaga game code reads from any external devices.
            switch (deviceID)
            {
                default:
                    Console.WriteLine($"WARNING: An IN/Read for port {deviceID} is not implemented.");
                    return 0x00;
            }
        }

        /**
         * Used to handle the CPU's OUT instruction; write value to given device ID.
         */
        private void CPU_OnDeviceWrite(int deviceID, byte data)
        {
            switch (deviceID)
            {
                // The Pac-Man game code writes data to port zero, which is used as the lower 8 bits of an
                // address when using interrupt mode 2 (IM2). We save this value so we can use it when triggering
                // the VBLANK interrupt.
                case 0x00:
                    _port0WriteLastData = data;
                    break;

                default:
                    Console.WriteLine($"WARNING: An OUT/Write for port {deviceID} (value: {data}) is not implemented.");
                    break;
            }
        }

        #endregion

        #region Memory Read/Write Implementation (IMemory)

        public byte Read(int address)
        {
            // TODO: Determine memory map... have an IMemory implementation per CPU?
            if (address >= 0x0000 && address <= 0x3FFF)
            {
                return _memory[address];
            }
            else if (address < 0x00)
            {
                throw new IndexOutOfRangeException(String.Format("Invalid read memory address (< 0x0000): 0x{0:X4}", address));
            }
            else
            {
                // TODO: I may need to remove and/or relax this restriction. Adding an exception for now
                // so I can troubleshoot while getting things running.
                throw new Exception(String.Format("Unexpected read memory address: 0x{0:X4}", address));
            }
        }

        public ushort Read16(int address)
        {
            var lower = Read(address);
            var upper = Read(address + 1) << 8;
            return (UInt16)(upper | lower);
        }

        public void Write(int address, byte value)
        {
            // TODO: Determine memory map... have an IMemory implementation per CPU?
            if (address >= 0x0000 && address <= 0x3FFF)
            {
                if (AllowWritableROM)
                    _memory[address] = value;
                else
                    throw new Exception(String.Format("Unexpected write to ROM region (0x0000 - 0x3FFF): {0:X4}", address));
            }
            else
            {
                // Writing to any other locations will do nothing.
                // TODO: Throw an exception during development so I can troubleshoot while getting things running.
                // Console.WriteLine(String.Format("Unexpected write to memory address: 0x{0:X4} with value: 0x{1:X2}", address, value));
                throw new Exception(String.Format("Unexpected write to memory address: 0x{0:X4} with value: 0x{1:X2}", address, value));
            }
        }

        public void Write16(int address, ushort value)
        {
            var lower = (byte)(value & 0x00FF);
            var upper = (byte)((value & 0xFF00) >> 8);
            Write(address, lower);
            Write(address + 1, upper);
        }

        #endregion

        #region Private Methods: Hardware Loop

        /**
         * Handles stepping the CPU to execute instructions as well as firing interrupts.
         */
        private void HardwareLoop()
        {
            _cpuStopWatch.Start();
            _cycleCount = 0;

#if !DEBUG
            try
            {
#endif
                while (!_cancelled)
                {
                    while (_paused)
                        Thread.Sleep(250);

                    // Handle all the debug tasks that need to happen before we execute an instruction.
                    if (Debug)
                    {
                        HandleDebugFeaturesPreStep();

                        // If the interactive debugger is active, wait for the user to single step, continue,
                        // or perform another operation.
                        while(_isWaitingForInteractiveDebugger)
                            Thread.Sleep(250);
                    }

                    // Step the CPU to execute the next instruction.
                    var cycles = _cpu.Step();

                    // Keep track of the number of cycles to see if we need to throttle the CPU.
                    _cycleCount += cycles;

                    // Handle all the debug tasks that need to happen after we execute an instruction.
                    if (Debug)
                        HandleDebugFeaturesPostStep(cycles);

                    // Throttle the CPU emulation if needed.
                    if (_cycleCount >= (CPU_HZ/60))
                    {
                        _cpuStopWatch.Stop();

                        if (_cpuStopWatch.Elapsed.TotalMilliseconds < 16.6)
                        {
                            var sleepForMs = 16.6 - _cpuStopWatch.Elapsed.TotalMilliseconds;

                            if (sleepForMs >= 1)
                                System.Threading.Thread.Sleep((int)sleepForMs);
                        }

                        _cycleCount = 0;
                        _cpuStopWatch.Restart();
                    }

                    // Fire a CPU interrupt if it's time to do so.
                    HandleInterrupts(cycles);
                }
#if !DEBUG
            }
            catch (Exception exception)
            {
                Console.WriteLine("-------------------------------------------------------------------");
                Console.WriteLine("An exception occurred during emulation!");
                _cpu.PrintDebugSummary();
                Console.WriteLine("-------------------------------------------------------------------");
                throw exception;
            }
#endif

            _cpu = null;
            _thread = null;
        }

        // TODO: Verify this existing implementation also applies to Galaga.
        // I believe an interrupt should fire on CPU 1 and 2: https://github.com/mamedev/mame/blob/master/src/mame/drivers/galaga.cpp#L1558-L1565
        /**
         * Galaga sends a single maskable interrupt, which is driven by the video hardware.
         * We can use the number of CPU cycles elapsed to roughly estimate when this interrupt
         * should fire which is roughly 60hz (also known as vblank; when the electron beam reaches
         * end of the screen). Returns true if it was time to fire an interrupt and false otherwise.
         * Note that a true return value does not mean that an interrupt fired (interrupts can be
         * disabled), only that it was time to fire one.
         */
        private bool HandleInterrupts(int cyclesElapsed)
        {
            // Keep track of the number of cycles since the last interrupt occurred.
            _cyclesSinceLastInterrupt += cyclesElapsed;

            // Determine if it's time for the video hardware to fire an interrupt.
            if (_cyclesSinceLastInterrupt < _cyclesPerInterrupt)
                return false;

            // CRT electron beam reached the end (V-Blank).

            // If interrupts are enabled, then handle them, otherwise do nothing.
            if (_cpu.InterruptsEnabled)
            {
                // If we're going to run an interrupt handler, ensure interrupts are disabled.
                // This ensures we don't interrupt the interrupt handler. The program ROM will
                // re-enable the interrupts manually.
                _cpu.InterruptsEnabled = false;

                // Execute the handler for the interrupt.
                _cpu.StepMaskableInterrupt(_port0WriteLastData);

                // Every 1/60 of a second is a good time for us to generate a video frame as
                // well as all of the audio samples that need to be queued up to play.
                HandleRenderVideoFrame();
                HandleRenderAudioSamples();
            }

            // Reset the count so we can count up again.
            _cyclesSinceLastInterrupt = 0;

            return true;
        }

        private void HandleRenderVideoFrame()
        {
            /* TODO: Video hardware.
            // Render the screen into an image.
            var image = _video.Render(this, _spriteCoordinates, _flipScreen);

            // Convert the image into a bitmap format.

            byte[] bitmap = null;

            using (var steam = new MemoryStream())
            {
                image.Save(steam, new SixLabors.ImageSharp.Formats.Bmp.BmpEncoder());
                bitmap = steam.ToArray();
            }

            // Delegate to the render event, passing the framebuffer to be rendered.
            _renderEventArgs.FrameBuffer = bitmap;
            OnRender?.Invoke(_renderEventArgs);
            */
        }

        private void HandleRenderAudioSamples()
        {
            /* TODO: Audio hardware.
            var samples = new byte[_audioSamplesPerFrame][];

            // Generate the number of audio samples that we need for a given "frame".
            for (var i = 0; i < _audioSamplesPerFrame; i++)
            {
                var sample = _audio.Tick();
                samples[i] = sample;
            }

            // Delegate to the event, passing the audio samples to be played.
            _audioSampleEventArgs.Samples = samples;
            OnAudioSample?.Invoke(_audioSampleEventArgs);
            */
        }

        /**
         * This method handles all the work that needs to be done when debugging is enabled right before
         * the CPU executes an opcode. This includes recording CPU stats, address history, and CPU breakpoints,
         * as well as implements the interactive debugger.
         */
        private void HandleDebugFeaturesPreStep()
        {
            // Record the current address.

            _addressHistory.Add(_cpu.Registers.PC);

            if (_addressHistory.Count >= MAX_ADDRESS_HISTORY)
                _addressHistory.RemoveAt(0);

            // See if we need to break based on a given address.
            if (BreakAtAddresses.Contains(_cpu.Registers.PC))
                _singleStepping = true;

            // If we need to break, print out the CPU state and wait for a keypress.
            if (_singleStepping)
            {
                Break();
                return;
            }
        }

        /**
         * This method handles all the work that needs to be done when debugging is enabled right after
         * the CPU executes an opcode. This includes recording CPU stats and reverse step history.
         */
        private void HandleDebugFeaturesPostStep(int cyclesElapsed)
        {
            // Keep track of the total number of steps (instructions) and cycles ellapsed.
            _totalOpcodes++;
            _totalCycles += cyclesElapsed;

            // Used to slow down the emulation to watch the renderer.
            // if (_totalCycles % 1000 == 0)
            //     System.Threading.Thread.Sleep(10);

            if (ReverseStepEnabled)
            {
                if (_executionHistory.Count >= MAX_REVERSE_STEP_HISTORY)
                    _executionHistory.RemoveAt(0);

                var state = SaveState();
                state.LastCyclesExecuted = cyclesElapsed;

                _executionHistory.Add(state);
            }
        }

        #endregion

        #region Public Methods: Save/Load State

        /**
         * Used to dump the state of the CPU and all fields needed to restore the emulator's
         * state in order to continue at this execution point later on.
         */
        public EmulatorState SaveState()
        {
            return new EmulatorState()
            {
                Registers = _cpu.Registers,
                Flags = _cpu.Flags,
                Halted = _cpu.Halted,
                InterruptsEnabled = _cpu.InterruptsEnabled,
                InterruptsEnabledPreviousValue = _cpu.InterruptsEnabledPreviousValue,
                InterruptMode = _cpu.InterruptMode,
                Memory = _memory,
                // SpriteCoordinates = _spriteCoordinates,
                TotalCycles = _totalCycles,
                TotalOpcodes = _totalOpcodes,
                CyclesSinceLastInterrupt = _cyclesSinceLastInterrupt,
                // AudioHardwareState = _audio.SaveState(),
            };
        }

        /**
         * Used to restore the state of the CPU and restore all fields to allow the emulator
         * to continue execution from a previously saved state.
         */
        public void LoadState(EmulatorState state)
        {
            _cpu.Registers = state.Registers;
            _cpu.Flags = state.Flags;
            _cpu.Halted = state.Halted;
            _cpu.InterruptsEnabled = state.InterruptsEnabled;
            _cpu.InterruptsEnabledPreviousValue = state.InterruptsEnabledPreviousValue;
            _cpu.InterruptMode = state.InterruptMode;
            _memory = state.Memory;
            // _spriteCoordinates = state.SpriteCoordinates;
            _totalCycles = state.TotalCycles;
            _totalOpcodes = state.TotalOpcodes;
            _cyclesSinceLastInterrupt = state.CyclesSinceLastInterrupt;
            // _audio.LoadState(state.AudioHardwareState);
        }

        #endregion
    }
}
