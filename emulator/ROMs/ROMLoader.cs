using System;
using System.Collections.Generic;
using System.IO;

namespace JustinCredible.GalagaEmu
{
    /**
     * Used to load a set of ROMs into memory.
     */
    public class ROMLoader
    {
        public static ROMData LoadFromDisk(ROMSet romset, string directoryPath, bool enforceValidChecksum = true)
        {
            var romData = new ROMData();

            List<ROMFile> romFiles = null;

            if (romset == ROMSet.GalagaNamcoRevB)
                romFiles = ROMs.GALAGA_NAMCO_REV_B;
            else if (romset == ROMSet.GalagaNamco)
                romFiles = ROMs.GALAGA_NAMCO;
            else if (romset == ROMSet.GalagaMidwaySet1)
                romFiles = ROMs.GALAGA_MIDWAY_SET_1;
            else if (romset == ROMSet.GalagaMidwaySet2)
                romFiles = ROMs.GALAGA_MIDWAY_SET_2;
            // else if (romset == ROMSet.GalagaMidwaySet1FastShoot)
            //     romFiles = ROMs.GALAGA_MIDWAY_SET_1_FAST_SHOOT;
            else
                throw new ArgumentException($"Unexpected romset: {romset}");

            foreach (var romFile in romFiles)
            {
                var path = Path.Combine(directoryPath, romFile.FileName);
                string alternatePath = null;

                if (!String.IsNullOrWhiteSpace(romFile.AlternateFileName))
                    alternatePath = Path.Combine(directoryPath, romFile.AlternateFileName);

                byte[] rom = null;

                // Attempt to load the ROM file data from the primary and secondary file names.

                if (File.Exists(path))
                    rom = File.ReadAllBytes(path);

                if (!String.IsNullOrWhiteSpace(alternatePath) && File.Exists(alternatePath))
                    rom = File.ReadAllBytes(alternatePath);

                var alternateFileNameMessage = romFile.AlternateFileName == null ? "" : $"(or alternate name '{romFile.AlternateFileName}')";

                if (rom == null)
                    throw new Exception($"Could not locate the '{romFile.Description}' ROM file '{romFile.FileName}'{alternateFileNameMessage} with CRC32 of '{romFile.CRC32}' at the location: {path}");

                // The ROM size should always match.

                if (romFile.Size != rom.Length)
                    throw new Exception($"The file size for '{romFile.Description}' ROM file '{romFile.FileName}'{alternateFileNameMessage} at the location: {path} was {rom.Length} bytes, but we are expecting {romFile.Size} bytes.");

                // Perform a quick checksum to determine if we got the correct file.

                var crc32 = new CRC32();
                var checksum = crc32.Get(rom).ToString("X8");
                var checksumMatches = checksum.Equals(romFile.CRC32, StringComparison.OrdinalIgnoreCase);

                if (!checksumMatches)
                {
                    var message = $"The CRC32 checksum for '{romFile.Description}' ROM file '{romFile.FileName}'{alternateFileNameMessage} at the location: {path} was calculated as '{checksum}', but we are expecting '{romFile.CRC32}'.";

                    if (enforceValidChecksum)
                        throw new Exception(message);
                    else
                        Console.WriteLine($"[WARNING] {message}");
                }

                // Add the binary ROM data to the set indexed by identifier enum.
                romData.Data[romFile.ID] = rom;
            }

            return romData;
        }
    }
}
