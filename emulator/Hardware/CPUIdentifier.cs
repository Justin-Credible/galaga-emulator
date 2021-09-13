using System;

namespace JustinCredible.GalagaEmu
{
    /**
     * Identifies each of the three Z80 CPUs used by Galaga.
     */
    public enum CPUIdentifier
    {
        /**
         * The main game controller CPU. This CPU dispatches work to
         * the second CPU as needed.
         */
        CPU1_MainController = 1,

        /**
         * The secondary CPU which handles work as assigned by the main CPU.
         */
        CPU2_GameHelper = 2,

        /**
         * This CPU is dedicated to audio.
         */
        CPU3_SoundProcessor = 3,
    }
}
