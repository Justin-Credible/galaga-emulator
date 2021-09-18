using System.Collections.Generic;

namespace JustinCredible.GalagaEmu
{
    public class ROMData
    {
        public Dictionary<ROMIdentifier, byte[]> Data { get; set; } = new Dictionary<ROMIdentifier, byte[]>();
    }
}
