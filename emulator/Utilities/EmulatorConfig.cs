using System;
using System.Collections.Generic;

namespace JustinCredible.GalagaEmu
{
    public class EmulatorConfig
    {
        public string RomPath { get; set; }
        public ROMSet RomSet { get; set; }
        public string DipSwitchesConfigPath { get; set; }
        public string LoadStateFilePath { get; set; }
        public bool SkipChecksums { get; set; }
        public bool WritableRom { get; set; }
        public bool Debug { get; set; }
        public List<UInt16> Breakpoints { get; set; }
        public List<UInt16> BreakpointsCpu1 { get; set; }
        public List<UInt16> BreakpointsCpu2 { get; set; }
        public List<UInt16> BreakpointsCpu3 { get; set; }
        public bool ReverseStep { get; set; }
        public string AnnotationsCpu1FilePath { get; set; }
        public string AnnotationsCpu2FilePath { get; set; }
        public string AnnotationsCpu3FilePath { get; set; }
    }
}
