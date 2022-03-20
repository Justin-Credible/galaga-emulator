
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
    public class GalagaPCB : IMemoryMap
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
        public delegate void BreakpointHitEvent(CPUIdentifier? cpuID);
        public event BreakpointHitEvent OnBreakpointHitEvent;

        #endregion

        #region Hardware: Components

        // The Zilog Z80 CPUs for the Galaga hardware are all clocked at 3.072MHz.
        private const int CPU_HZ = 3072000;

        // The Namco MCUs runs at CPU_MHZ/6/2 = 1.536MHz.
        private const int NAMCO_MCU_HZ = CPU_HZ / 6 / 2;

        // Galaga uses three Z80 CPUs.
        internal CPU _cpu1; // Main Controller
        internal CPU _cpu2; // Game Helper
        internal CPU _cpu3; // Sound Processor

        private bool _haltCpu2 = false;
        private bool _haltCpu3 = false;

        private VideoHardware _video;
        // private AudioHardware _audio; // TODO
        public DIPSwitches DIPSwitchState { get; set; } = new DIPSwitches();
        public Buttons ButtonState { get; set; } = new Buttons();

        // There is 64KB of address space available, but only ~5KB is mapped to RAM/VRAM. However, we'll
        // make the array the full size so that we don't have to do address translations when performing I/O.
        private const int ADDRESS_SPACE = 64 * 1024;

        // This will hold the shared RAM and VRAM regions for all CPUs.
        // http://www.arcaderestoration.com/memorymap/3291/Galaga.aspx
        // https://github.com/mamedev/mame/blob/cf56aee3df86cc089acc6238f80142222bd3255b/src/mame/drivers/galaga.cpp#L186-L262
        private byte[] _memory = null; // 64 KB of address space

        // Each CPU has its own specific ROMs mapped into its address space.
        private byte[] _cpu1Rom = null; // 16 KB
        private byte[] _cpu2Rom = null; // 1 KB
        private byte[] _cpu3Rom = null; // 1 KB

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
        public List<UInt16> BreakAtAddressesCPU1 { get; set; } = new List<ushort>();
        public List<UInt16> BreakAtAddressesCPU2 { get; set; } = new List<ushort>();
        public List<UInt16> BreakAtAddressesCPU3 { get; set; } = new List<ushort>();

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
         * a map of memory addresses to string annotation values per CPU.
         */
        public Dictionary<CPUIdentifier, Dictionary<UInt16, String>> Annotations { get; set; }

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

            // The initial configuration of the CPUs.

            var cpu1Config = new CPUConfig()
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

            var cpu2Config = new CPUConfig()
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

            var cpu3Config = new CPUConfig()
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
            _cpu1 = new CPU(cpu1Config);
            _cpu1.OnDeviceRead += (int deviceID) => CPU_OnDeviceRead(CPUIdentifier.CPU1_MainController, deviceID);
            _cpu1.OnDeviceWrite += (int deviceID, byte data) => CPU_OnDeviceWrite(CPUIdentifier.CPU1_MainController, deviceID, data);
            _cpu2 = new CPU(cpu2Config);
            _cpu2.OnDeviceRead += (int deviceID) => CPU_OnDeviceRead(CPUIdentifier.CPU2_GameHelper, deviceID);
            _cpu2.OnDeviceWrite += (int deviceID, byte data) => CPU_OnDeviceWrite(CPUIdentifier.CPU2_GameHelper, deviceID, data);
            _cpu3 = new CPU(cpu3Config);
            _cpu3.OnDeviceRead += (int deviceID) => CPU_OnDeviceRead(CPUIdentifier.CPU3_SoundProcessor, deviceID);
            _cpu3.OnDeviceWrite += (int deviceID, byte data) => CPU_OnDeviceWrite(CPUIdentifier.CPU3_SoundProcessor, deviceID, data);
            _cyclesSinceLastInterrupt = 0;

            // Initialize an array for the CPU's shared RAM and video RAM.
            _memory = new byte[ADDRESS_SPACE];

            // Fetch and load the ROM data; each CPU has 64KB of address space and the ROMs are mapped in to
            // each starting at address zero. CPU1 has 16KB of ROM, while CPU2/3 have 4KB.

            var cpu1CodeRom1 = romData.Data[ROMIdentifier.CPU1_CODE1];
            var cpu1CodeRom2 = romData.Data[ROMIdentifier.CPU1_CODE2];
            var cpu1CodeRom3 = romData.Data[ROMIdentifier.CPU1_CODE3];
            var cpu1CodeRom4 = romData.Data[ROMIdentifier.CPU1_CODE4];
            var cpu2CodeRom = romData.Data[ROMIdentifier.CPU2_CODE];
            var cpu3CodeRom = romData.Data[ROMIdentifier.CPU3_CODE];

            _cpu1Rom = new byte[(cpu1CodeRom1.Length + cpu1CodeRom2.Length + cpu1CodeRom3.Length + cpu1CodeRom4.Length)];
            _cpu2Rom = new byte[cpu2CodeRom.Length];
            _cpu3Rom = new byte[cpu3CodeRom.Length];

            Array.Copy(cpu1CodeRom1, 0, _cpu1Rom, 0, cpu1CodeRom1.Length);
            Array.Copy(cpu1CodeRom2, 0, _cpu1Rom, cpu1CodeRom1.Length, cpu1CodeRom2.Length);
            Array.Copy(cpu1CodeRom3, 0, _cpu1Rom, cpu1CodeRom1.Length + cpu1CodeRom2.Length, cpu1CodeRom3.Length);
            Array.Copy(cpu1CodeRom4, 0, _cpu1Rom, cpu1CodeRom1.Length + cpu1CodeRom2.Length + cpu1CodeRom3.Length, cpu1CodeRom4.Length);
            Array.Copy(cpu2CodeRom, 0, _cpu2Rom, 0, cpu2CodeRom.Length);
            Array.Copy(cpu3CodeRom, 0, _cpu3Rom, 0, cpu3CodeRom.Length);

            // This class implements the IMemoryMap interface, which the CPU needs to determine how to read and
            // write data. We set the reference to this class instance (whose implementation uses _memory).
            _cpu1.Memory = new MemoryMapper(CPUIdentifier.CPU1_MainController, this);
            _cpu2.Memory = new MemoryMapper(CPUIdentifier.CPU2_GameHelper, this);
            _cpu3.Memory = new MemoryMapper(CPUIdentifier.CPU3_SoundProcessor, this);

            // Initialize video hardware.
            _video = new VideoHardware(romData, ROMSet);
            _video.Initialize();

            // Initialize audio hardware. // TODO
            // _audio = new AudioHardware(romData, ROMSet);

            // TODO: Initialize Namco 51XX (input)
            // TODO: Initialize Namco 54XX (noise generator)
            // TODO: Initialize 05XX (starfield generator)

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

            #if DEBUG
            Console.WriteLine("GalagaPCB - Stop requested.");
            #endif

            _cancelled = true;
        }

        /**
         * Used to temporarily stop execution of the CPU. Resume with a call to UnPause().
         */
        public void Pause()
        {
            #if DEBUG
            Console.WriteLine("GalagaPCB - Pause requested.");
            #endif

            _paused = true;
        }

        /**
         * Used to resume execution of the CPU after a call to Pause().
         */
        public void UnPause()
        {
            #if DEBUG
            Console.WriteLine("GalagaPCB - UnPause requested.");
            #endif

            _paused = false;
        }

        /**
         * Used to stop CPU execution and enable single stepping through opcodes via the interactive
         * debugger (only when Debug = true).
         */
        public void Break(CPUIdentifier? cpuThatTriggeredBreak = null)
        {
            if (!Debug)
                return;

            #if DEBUG
            Console.WriteLine("GalagaPCB - Break requested.");
            #endif

            _singleStepping = true;

            if (OnBreakpointHitEvent != null)
            {
                _isWaitingForInteractiveDebugger = true;
                OnBreakpointHitEvent.Invoke(cpuThatTriggeredBreak);
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

            #if DEBUG
            Console.WriteLine("GalagaPCB - Continue requested.");
            #endif

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
            // TODO: Decide if it's worth implementing reverse step for all 3 CPU instances.
            throw new NotImplementedException();

            /*
            if (!Debug && !ReverseStepEnabled)
                throw new Exception("Debug feature: reverse stepping is not enabled.");

            #if DEBUG
            Console.WriteLine("GalagaPCB - Reverse step requested.");
            #endif

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
            */
        }

        #endregion

        #region CPU Event Handlers

        /**
         * Used to handle the CPU's IN instruction; read value from given device ID.
         */
        private byte CPU_OnDeviceRead(CPUIdentifier cpuID, int deviceID)
        {
            if (cpuID != CPUIdentifier.CPU1_MainController)
                throw new NotImplementedException($"Device read for CPU{(int)cpuID} not implemented.");

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
        private void CPU_OnDeviceWrite(CPUIdentifier cpuID, int deviceID, byte data)
        {
            if (cpuID != CPUIdentifier.CPU1_MainController)
                throw new NotImplementedException($"Device write for CPU{(int)cpuID} not implemented.");

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

        public byte Read(CPUIdentifier cpuID, int address)
        {
            if (address >= 0x0000 && address <= 0x3FFF)
            {
                switch (cpuID)
                {
                    case CPUIdentifier.CPU1_MainController:
                    {
                        // CPU1 has 16KB of ROM starting at address zero.
                        return _cpu1Rom[address];
                    }
                    case CPUIdentifier.CPU2_GameHelper:
                    {
                        // Sanity check; CPU2/3 only has 4KB of ROM so I don't expect this to hit.
                        if (address >= 0x1000)
                        {
                            // throw new Exception(String.Format("Unexpected read memory address for CPU{1}: 0x{0:X4}", address, (int)cpuID));
                            #if DEBUG
                            Console.WriteLine(String.Format("Unexpected read from range 0x1000 - 01x3FFF (0x{0:X4}) for CPU{1}; returning 0x00", address, (int)cpuID));
                            #endif
                            return 0x00;
                        }

                        return _cpu2Rom[address];
                    }
                    case CPUIdentifier.CPU3_SoundProcessor:
                    {
                        // Sanity check; CPU2/3 only has 4KB of ROM so I don't expect this to hit.
                        if (address >= 0x1000)
                            throw new Exception(String.Format("Unexpected read memory address for CPU{1}: 0x{0:X4}", address, (int)cpuID));

                        return _cpu3Rom[address];
                    }
                    default:
                        throw new NotImplementedException($"Read for CPU{(int)cpuID} is not implemented.");
                }
            }
            else if (address >= 0x6800 && address <= 0x6807)
            {
                // 8 Bytes: DIP Switches ("bosco_dsw_r")
                // TODO: Implement DIP switch settings: http://www.arcaderestoration.com/gamedips/3291/All/Galaga.aspx
                // There are two DIP switch banks (A and B); each has 8 switches.
                // Each address indicates a switch A and B pair; e.g. 0x6800 => SWA 1 and SWB 1, 0x6804 => SWA 5 and SWB 5.
                // For the return value, bit 0 is for SWB and bit 1 is for SWA.
                // NOTE: A zero indcates the switch is ON and a 1 indicates it is OFF (???)
                if (address == 0x6804)
                    return 0b00000010; // SWB 5: Freeze: Off
                else
                    return 0x00;
            }
            else if (address >= 0x7000 && address <= 0x7100)
            {
                // 256 + 1 Bytes for 06xx Bus Interface; attaches to Namco51xx (inputs etc) and Namco54xx (noise generator).
                // TODO.
                #if DEBUG
                Console.WriteLine(String.Format("Read from 06xx Bus interface range 0x7000 - 0x7100 (0x{0:X4}) for CPU{1}; returning 0x00", address, (int)cpuID));
                #endif
                //return 0x00;
                // HACK: CPU1 waits for the IO processor to complete with status 0x10
                return 0x10;
            }
            else if (address >= 0x8000 && address <= 0x87FF)
            {
                // 2KB; Shared VRAM
                return _memory[address];
            }
            else if (address >= 0x8800 && address <= 0x8BFF)
            {
                // 1KB; Shared RAM 1
                return _memory[address];
            }
            else if (address >= 0x9000 && address <= 0x93FF)
            {
                // 1KB; Shared RAM 2
                return _memory[address];
            }
            else if (address >= 0x9800 && address <= 0x9BFF)
            {
                // 1KB; Shared RAM 3
                return _memory[address];
            }
            else if (address < 0x00)
            {
                throw new IndexOutOfRangeException(String.Format("Invalid read memory address (< 0x0000) for CPU{1}: 0x{0:X4}", address, (int)cpuID));
            }
            else
            {
                // TODO: I may need to remove and/or relax this restriction. Adding an exception for now
                // so I can troubleshoot while getting things running.
                throw new Exception(String.Format("Unexpected read memory address for CPU{1}: 0x{0:X4}", address, (int)cpuID));
            }
        }

        public ushort Read16(CPUIdentifier cpuID, int address)
        {
            var lower = Read(cpuID, address);
            var upper = Read(cpuID, address + 1) << 8;
            return (UInt16)(upper | lower);
        }

        public void Write(CPUIdentifier cpuID, int address, byte value)
        {
            if (address >= 0x0000 && address <= 0x3FFF)
            {
                // TODO: The following page indicates that a write to the ROM region is a NOP.
                // So I may need to remove this exception, but will leave in place for now so I can debug.
                // http://www.arcaderestoration.com/memorymap/3291/Galaga.aspx
                if (!AllowWritableROM)
                    throw new Exception(String.Format("Unexpected write to ROM region (0x0000 - 0x3FFF) for CPU{1}: {0:X4}", address, (int)cpuID));

                switch (cpuID)
                {
                    case CPUIdentifier.CPU1_MainController:
                    {
                        // CPU1 has 16KB of ROM starting at address zero.
                        _cpu1Rom[address] = value;
                        break;
                    }
                    case CPUIdentifier.CPU2_GameHelper:
                    {
                        // Sanity check; CPU2/3 only has 4KB of ROM so I don't expect this to hit.
                        if (address >= 0x1000)
                            throw new Exception(String.Format("Unexpected read memory address for CPU{1}: 0x{0:X4}", address, (int)cpuID));

                        _cpu2Rom[address] = value;
                        break;
                    }
                    case CPUIdentifier.CPU3_SoundProcessor:
                    {
                        // Sanity check; CPU2/3 only has 4KB of ROM so I don't expect this to hit.
                        if (address >= 0x1000)
                            throw new Exception(String.Format("Unexpected read memory address for CPU{1}: 0x{0:X4}", address, (int)cpuID));

                        _cpu3Rom[address] = value;
                        break;
                    }
                    default:
                        throw new NotImplementedException($"Write for CPU{(int)cpuID} is not implemented.");
                }
            }
            else if (address >= 0x6800 && address <= 0x681F)
            {
                // 32 Bytes for Audio I/O: Waveform Sound Generator (WSG)
                // TODO: Is this the same as the WSG3 used in Pac-Man?
                #if DEBUG
                // Console.WriteLine(String.Format("Write to Audio I/O range 0x6800 - 0x681E (0x{0:X4}) for CPU{2} with value 0x{1:X2}", address, value, (int)cpuID));
                #endif
            }
            else if (address >= 0x6820 && address <= 0x6827)
            {
                // 8 Bytes: Latches ("bosco_latch_w").
                switch (address)
                {
                    case 0x6820:
                        // IRQ1: main CPU (CPU1) irq enable/acknowledge
                        // TODO
                        #if DEBUG
                        Console.WriteLine(String.Format("'IRQ1: main CPU (CPU1) irq enable/acknowledge' write at address 0x{0:X4} with value for CPU{2}: 0x{1:X2}", address, value, (int)cpuID));
                        #endif
                        _cpu1.InterruptsEnabled = value != 0;
                        break;
                    case 0x6821:
                        // IRQ2: motion CPU (CPU2) irq enable/acknowledge
                        // TODO
                        #if DEBUG
                        Console.WriteLine(String.Format("'IRQ2: motion CPU (CPU2) irq enable/acknowledge' write at address 0x{0:X4} with value for CPU{2}: 0x{1:X2}", address, value, (int)cpuID));
                        #endif
                        _cpu2.InterruptsEnabled = value != 0;
                        break;
                    case 0x6822:
                        // NMION: sound CPU (CPU3) nmi enable
                        // TODO
                        #if DEBUG
                        // Console.WriteLine(String.Format("'NMION: sound CPU (CPU3) nmi enable' write at address 0x{0:X4} with value for CPU{2}: 0x{1:X2}", address, value, (int)cpuID));
                        #endif
                        _cpu3.InterruptsEnabled = value == 0;
                        break;
                    case 0x6823:
                        // RESET: reset sub and sound CPU, and 5xXX chips on CPU board
                        // NOTE: Disassembly says "Halt CPUs 2 and 3" when loading with zero @ $336D?
                        // I'm assuming 0 means halt and non-zero means un-halt; this seems to get past initialization.
                        _haltCpu2 = value == 0;
                        _haltCpu3 = value == 0;
                        break;
                    default:
                        // 0x6824 n.c.
                        // 0x6825 MOD 0 unused ?
                        // 0x6826 MOD 1 unused ?
                        // 0x6827 MOD 1 unused ?
                        throw new NotImplementedException(String.Format("Write to bosco_latch_w range 0x6820 - 0x6827 (0x{0:X4}) for CPU{2} is not implemented; value: 0x{1:X2}", address, value, (int)cpuID));
                }
            }
            else if (address == 0x6830)
            {
                // 1 Byte: Watchdog reset (not implemented in this emulator)
                // no-op
            }
            else if (address >= 0x7000 && address <= 0x7100)
            {
                // 256 + 1 Bytes for 06xx Bus Interface; attaches to Namco51xx (inputs etc) and Namco54xx (noise generator).
                // TODO.
                #if DEBUG
                Console.WriteLine(String.Format("Write for 06xx Bus interface range 0x7000 - 0x7100 (0x{0:X4}) for CPU{2} with value 0x{1:X2}", address, value, (int)cpuID));
                #endif
            }
            else if (address >= 0x8000 && address <= 0x87FF)
            {
                // 2KB; Video RAM
                _memory[address] = value;
            }
            else if (address >= 0x8800 && address <= 0x8BFF)
            {
                // 1KB; Shared RAM 1
                _memory[address] = value;
            }
            else if (address >= 0x9000 && address <= 0x93FF)
            {
                // 1KB; Shared RAM 2
                _memory[address] = value;
            }
            else if (address >= 0x9800 && address <= 0x9BFF)
            {
                // 1KB; Shared RAM 3
                _memory[address] = value;
            }
            else if (address >= 0xA000 && address <= 0xA005)
            {
                // 6 Bytes; Starfield Generator control
                // TODO.
                #if DEBUG
                Console.WriteLine(String.Format("Write for Starfield generator 0xA000 - 0xA005 (0x{0:X4}) for CPU{2} with value 0x{1:X2}", address, value, (int)cpuID));
                #endif
            }
            else if (address >= 0xA007)
            {
                // 1 Byte; Flip Screen.
                // TODO.
                #if DEBUG
                Console.WriteLine(String.Format("Write for flip screen 0xA007 for CPU{1} with value 0x{0:X2}", value, (int)cpuID));
                #endif
            }
            else
            {
                // Writing to any other locations will do nothing.
                // TODO: Throw an exception during development so I can troubleshoot while getting things running.
                // Console.WriteLine(String.Format("Unexpected write to memory address: 0x{0:X4} with value: 0x{1:X2} for CPU{2}", address, value, (int)cpuID));
                throw new Exception(String.Format("Unexpected write to memory address: 0x{0:X4} with value: 0x{1:X2} for CPU{2}", address, value, (int)cpuID));
            }
        }

        public void Write16(CPUIdentifier cpuID, int address, ushort value)
        {
            var lower = (byte)(value & 0x00FF);
            var upper = (byte)((value & 0xFF00) >> 8);
            Write(cpuID, address, lower);
            Write(cpuID, address + 1, upper);
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
                    // TODO: Since I'm only looking at cycles on CPU1, the other CPUs may not be
                    // throttled properly. I'm hoping this won't matter and it will be "good enough".

                    var cycles = _cpu1.Step();

                    if (!_haltCpu2)
                    {
                        _cpu2.Step();
                    }

                    if (!_haltCpu3)
                    {
                        _cpu3.Step();
                    }

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

            _cpu1 = null;
            _cpu2 = null;
            _cpu3 = null;
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

            // Every 1/60 of a second is a good time for us to generate a video frame as
            // well as all of the audio samples that need to be queued up to play.
            HandleRenderVideoFrame();
            HandleRenderAudioSamples();

            // If interrupts are enabled, then handle them, otherwise do nothing.
            if (_cpu1.InterruptsEnabled)
            {
                // If we're going to run an interrupt handler, ensure interrupts are disabled.
                // This ensures we don't interrupt the interrupt handler. The program ROM will
                // re-enable the interrupts manually.
                _cpu1.InterruptsEnabled = false;

                // Execute the handler for the interrupt.
                _cpu1.StepMaskableInterrupt(_port0WriteLastData);
            }

            // If interrupts are enabled, then handle them, otherwise do nothing.
            if (_cpu2.InterruptsEnabled)
            {
                // If an interrupt is fired, then the CPU will need to resume if it was halted
                // in order to execute the interrupt handler.
                _haltCpu2 = false;

                // If we're going to run an interrupt handler, ensure interrupts are disabled.
                // This ensures we don't interrupt the interrupt handler. The program ROM will
                // re-enable the interrupts manually.
                _cpu2.InterruptsEnabled = false;

                // Execute the handler for the interrupt.
                // TODO: Technically this should be last write data for CPU2's port 0 write, but
                // since Galaga is not using interrupt modes zero or two, I don't think it matters.
                _cpu2.StepMaskableInterrupt(0x00);
            }

            // If interrupts are enabled, then handle them, otherwise do nothing.
            if (_cpu3.InterruptsEnabled)
            {
                // If an interrupt is fired, then the CPU will need to resume if it was halted
                // in order to execute the interrupt handler.
                _haltCpu3 = false;

                // If we're going to run an interrupt handler, ensure interrupts are disabled.
                // This ensures we don't interrupt the interrupt handler. The program ROM will
                // re-enable the interrupts manually.
                _cpu3.InterruptsEnabled = false;

                // Execute the handler for the interrupt.
                // TODO: Technically this should be last write data for CPU3's port 0 write, but
                // since Galaga is not using interrupt modes zero or two, I don't think it matters.
                _cpu3.StepNonMaskableInterrupt();
            }

            // Reset the count so we can count up again.
            _cyclesSinceLastInterrupt = 0;

            return true;
        }

        private void HandleRenderVideoFrame()
        {
            // Render the screen into an image.
            var image = _video.Render(_cpu1.Memory, null, false);

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
            // TODO: Add address history and breakpoints for CPU2/3?

            // Record the current address.

            _addressHistory.Add(_cpu1.Registers.PC);

            if (_addressHistory.Count >= MAX_ADDRESS_HISTORY)
                _addressHistory.RemoveAt(0);

            // See if we need to break based on a given address.

            CPUIdentifier? cpuThatTriggeredBreakpoint = null;

            // First check the shared breakpoint list.

            if (BreakAtAddresses.Contains(_cpu1.Registers.PC))
            {
                #if DEBUG
                Console.WriteLine(String.Format("Shared breakpoint list: PC for CPU1 is 0x{0:X4}; requesting single step.", _cpu1.Registers.PC));
                #endif
                _singleStepping = true;
                cpuThatTriggeredBreakpoint = CPUIdentifier.CPU1_MainController;
            }

            if (BreakAtAddresses.Contains(_cpu2.Registers.PC))
            {
                #if DEBUG
                Console.WriteLine(String.Format("Shared breakpoint list: PC for CPU2 is 0x{0:X4}; requesting single step.", _cpu2.Registers.PC));
                #endif
                _singleStepping = true;
                cpuThatTriggeredBreakpoint = CPUIdentifier.CPU2_GameHelper;
            }

            if (BreakAtAddresses.Contains(_cpu3.Registers.PC))
            {
                #if DEBUG
                Console.WriteLine(String.Format("Shared breakpoint list: PC for CPU3 is 0x{0:X4}; requesting single step.", _cpu3.Registers.PC));
                #endif
                _singleStepping = true;
                cpuThatTriggeredBreakpoint = CPUIdentifier.CPU3_SoundProcessor;
            }

            // Next check the breakpoint list specific to each CPU.
            // TODO: Remove the above and only have a list per CPU?

            if (BreakAtAddressesCPU1.Contains(_cpu1.Registers.PC))
            {
                #if DEBUG
                Console.WriteLine(String.Format("CPU1 breakpoint list: PC for CPU1 is 0x{0:X4}; requesting single step.", _cpu1.Registers.PC));
                #endif
                _singleStepping = true;
                cpuThatTriggeredBreakpoint = CPUIdentifier.CPU1_MainController;
            }

            if (BreakAtAddressesCPU2.Contains(_cpu2.Registers.PC))
            {
                #if DEBUG
                Console.WriteLine(String.Format("CPU2 breakpoint list: PC for CPU2 is 0x{0:X4}; requesting single step.", _cpu2.Registers.PC));
                #endif
                _singleStepping = true;
                cpuThatTriggeredBreakpoint = CPUIdentifier.CPU2_GameHelper;
            }

            if (BreakAtAddressesCPU3.Contains(_cpu3.Registers.PC))
            {
                #if DEBUG
                Console.WriteLine(String.Format("CPU3 breakpoint list: PC for CPU3 is 0x{0:X4}; requesting single step.", _cpu3.Registers.PC));
                #endif
                _singleStepping = true;
                cpuThatTriggeredBreakpoint = CPUIdentifier.CPU3_SoundProcessor;
            }

            // If we need to break, print out the CPU state and wait for a keypress.
            if (_singleStepping)
            {
                Break(cpuThatTriggeredBreakpoint);
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
                CPU1Registers = _cpu1.Registers,
                CPU1Flags = _cpu1.Flags,
                CPU1Halted = _cpu1.Halted,
                CPU1InterruptsEnabled = _cpu1.InterruptsEnabled,
                CPU1InterruptsEnabledPreviousValue = _cpu1.InterruptsEnabledPreviousValue,
                CPU1InterruptMode = _cpu1.InterruptMode,

                CPU2Registers = _cpu2.Registers,
                CPU2Flags = _cpu2.Flags,
                CPU2Halted = _cpu2.Halted,
                CPU2InterruptsEnabled = _cpu2.InterruptsEnabled,
                CPU2InterruptsEnabledPreviousValue = _cpu2.InterruptsEnabledPreviousValue,
                CPU2InterruptMode = _cpu2.InterruptMode,

                CPU3Registers = _cpu3.Registers,
                CPU3Flags = _cpu3.Flags,
                CPU3Halted = _cpu3.Halted,
                CPU3InterruptsEnabled = _cpu3.InterruptsEnabled,
                CPU3InterruptsEnabledPreviousValue = _cpu3.InterruptsEnabledPreviousValue,
                CPU3InterruptMode = _cpu3.InterruptMode,

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
            _cpu1.Registers = state.CPU1Registers;
            _cpu1.Flags = state.CPU1Flags;
            _cpu1.Halted = state.CPU1Halted;
            _cpu1.InterruptsEnabled = state.CPU1InterruptsEnabled;
            _cpu1.InterruptsEnabledPreviousValue = state.CPU1InterruptsEnabledPreviousValue;
            _cpu1.InterruptMode = state.CPU1InterruptMode;

            _cpu2.Registers = state.CPU2Registers;
            _cpu2.Flags = state.CPU2Flags;
            _cpu2.Halted = state.CPU2Halted;
            _cpu2.InterruptsEnabled = state.CPU2InterruptsEnabled;
            _cpu2.InterruptsEnabledPreviousValue = state.CPU2InterruptsEnabledPreviousValue;
            _cpu2.InterruptMode = state.CPU2InterruptMode;

            _cpu3.Registers = state.CPU3Registers;
            _cpu3.Flags = state.CPU3Flags;
            _cpu3.Halted = state.CPU3Halted;
            _cpu3.InterruptsEnabled = state.CPU3InterruptsEnabled;
            _cpu3.InterruptsEnabledPreviousValue = state.CPU3InterruptsEnabledPreviousValue;
            _cpu3.InterruptMode = state.CPU3InterruptMode;

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
