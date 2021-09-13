using System.Collections.Generic;

namespace JustinCredible.GalagaEmu
{
    /**
     * Used to encapsulate each of the ROMs for each game that runs on the Galaga hardware.
     */
    public class ROMs
    {
        public static readonly ROMFile NAMCO_REV_B_CPU1_CODE1 = new ROMFile() { FileName = "gg1_1b.3p", Size = 4096, CRC32 = "ab036c9f", Description = "CPU 1 - Code 1 (4KB)" };
        public static readonly ROMFile NAMCO_REV_B_CPU1_CODE2 = new ROMFile() { FileName = "gg1_2b.3m", Size = 4096, CRC32 = "d9232240", Description = "CPU 1 - Code 2 (4KB)" };
        public static readonly ROMFile NAMCO_REV_B_CPU1_CODE3 = new ROMFile() { FileName = "gg1_3.2m", Size = 4096, CRC32 = "753ce503", Description = "CPU 1 - Code 3 (4KB)" };
        public static readonly ROMFile NAMCO_REV_B_CPU1_CODE4 = new ROMFile() { FileName = "gg1_4b.2l", Size = 4096, CRC32 = "499fcc76", Description = "CPU 1 - Code 4 (4KB)" };
        public static readonly ROMFile NAMCO_REV_B_CPU2_CODE = new ROMFile() { FileName = "gg1_5b.3f", Size = 4096, CRC32 = "bb5caae3", Description = "CPU 2 - Code (4KB)" };
        public static readonly ROMFile NAMCO_REV_B_CPU3_CODE = new ROMFile() { FileName = "gg1_7b.2c", Size = 4096, CRC32 = "d016686b", Description = "CPU 3 - Code (4KB)" };
        public static readonly ROMFile NAMCO_REV_B_GFX1 = new ROMFile() { FileName = "gg1_9.4l", Size = 4096, CRC32 = "58b2f47c", Description = "Graphics 1 (4KB)" };
        public static readonly ROMFile NAMCO_REV_B_GFX2 = new ROMFile() { FileName = "gg1_11.4d", Size = 4096, CRC32 = "ad447c80", Description = "Graphics 2 (4KB)" };
        public static readonly ROMFile NAMCO_REV_B_GFX3 = new ROMFile() { FileName = "gg1_10.4f", Size = 4096, CRC32 = "dd6f1afc", Description = "Graphics 3 (4KB)" };
        public static readonly ROMFile NAMCO_REV_B_PALETTE = new ROMFile() { FileName = "prom-5.5n", Size = 32, CRC32 = "54603c6b", Description = "Palette PROM (32 bytes)" };
        public static readonly ROMFile NAMCO_REV_B_CHAR_LOOKUP = new ROMFile() { FileName = "prom-4.2n", Size = 256, CRC32 = "59b6edab", Description = "Char Lookup Table PROM (256 bytes)" };
        public static readonly ROMFile NAMCO_REV_B_SPRITE_LOOKUP = new ROMFile() { FileName = "prom-3.1c", Size = 256, CRC32 = "4a04bb6b", Description = "Sprite Lookup Table PROM (256 bytes)" };
        public static readonly ROMFile NAMCO_REV_B_PROM1 = new ROMFile() { FileName = "prom-1.1d", Size = 256, CRC32 = "7a2815b4", Description = "PROM 1 (256 bytes)" };
        public static readonly ROMFile NAMCO_REV_B_PROM2 = new ROMFile() { FileName = "prom-2.5c", Size = 256, CRC32 = "77245b66", Description = "PROM 1 (256 bytes)" };

        public static readonly ROMFile NAMCO_51XX = new ROMFile() { FileName = "51xx.bin", Size = 1024, CRC32 = "c2f57ef8", Description = "Namco 51XX (1KB)" };
        public static readonly ROMFile NAMCO_54XX = new ROMFile() { FileName = "54xx.bin", Size = 1024, CRC32 = "ee7357e0", Description = "Namco 54XX (1KB)" };

        public static readonly List<ROMFile> GALAGA_NAMCO_REV_B = new List<ROMFile>()
        {
            NAMCO_REV_B_CPU1_CODE1,
            NAMCO_REV_B_CPU1_CODE2,
            NAMCO_REV_B_CPU1_CODE3,
            NAMCO_REV_B_CPU1_CODE4,
            NAMCO_REV_B_CPU2_CODE,
            NAMCO_REV_B_CPU3_CODE,
            NAMCO_REV_B_GFX1,
            NAMCO_REV_B_GFX2,
            NAMCO_REV_B_GFX3,
            NAMCO_REV_B_PALETTE,
            NAMCO_REV_B_CHAR_LOOKUP,
            NAMCO_REV_B_SPRITE_LOOKUP,
            NAMCO_REV_B_PROM1,
            NAMCO_REV_B_PROM2,
            NAMCO_51XX,
            NAMCO_54XX,
        };
    }
}
