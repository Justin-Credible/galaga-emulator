using System;
using JustinCredible.ZilogZ80;

namespace JustinCredible.GalagaEmu
{
    public class MemoryMapper : IMemory
    {
        private CPUIdentifier _cpuID;
        private IMemoryMap _memoryMap;

        public MemoryMapper(CPUIdentifier cpuID, IMemoryMap memoryMap)
        {
            _cpuID = cpuID;
            _memoryMap = memoryMap;
        }

        public void Write(int address, byte value)
        {
            _memoryMap.Write(_cpuID, address, value);
        }

        public void Write16(int address, UInt16 value)
        {
            _memoryMap.Write16(_cpuID, address, value);
        }

        public byte Read(int address)
        {
            return _memoryMap.Read(_cpuID, address);
        }

        public UInt16 Read16(int address)
        {
            return _memoryMap.Read16(_cpuID, address);
        }
    }
}
