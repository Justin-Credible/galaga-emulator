using JustinCredible.ZilogZ80;

namespace JustinCredible.GalagaEmu
{
    public class EmulatorState
    {
        public CPURegisters CPU1Registers { get; set; }
        public ConditionFlags CPU1Flags { get; set; }
        public bool CPU1Halted { get; set; }
        public bool CPU1InterruptsEnabled { get; set; }
        public bool CPU1InterruptsEnabledPreviousValue { get; set; }
        public InterruptMode CPU1InterruptMode { get; set; }

        public CPURegisters CPU2Registers { get; set; }
        public ConditionFlags CPU2Flags { get; set; }
        public bool CPU2Halted { get; set; }
        public bool CPU2InterruptsEnabled { get; set; }
        public bool CPU2InterruptsEnabledPreviousValue { get; set; }
        public InterruptMode CPU2InterruptMode { get; set; }

        public CPURegisters CPU3Registers { get; set; }
        public ConditionFlags CPU3Flags { get; set; }
        public bool CPU3Halted { get; set; }
        public bool CPU3InterruptsEnabled { get; set; }
        public bool CPU3InterruptsEnabledPreviousValue { get; set; }
        public InterruptMode CPU3InterruptMode { get; set; }

        public byte[] Memory { get; set; }
        // public byte[] SpriteCoordinates { get; set; }
        public long TotalCycles { get; set; }
        public long TotalOpcodes { get; set; }
        public int CyclesSinceLastInterrupt { get; set; }
        // public AudioHardwareState AudioHardwareState { get; set; }

        public int? LastCyclesExecuted { get; set; }
    }
}
