using System;

namespace JustinCredible.GalagaEmu
{
    public interface IMemoryMap
    {
        void Write(CPUIdentifier cpuID, int address, byte value);
        void Write16(CPUIdentifier cpuID,int address, UInt16 value);
        byte Read(CPUIdentifier cpuID,int address);
        UInt16 Read16(CPUIdentifier cpuID,int address);
    }
}
