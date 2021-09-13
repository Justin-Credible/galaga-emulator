using System;

namespace JustinCredible.GalagaEmu
{
    public class RenderEventArgs : EventArgs
    {
        /**
         * The frame to be renderd to the screen in the Bitmap file format.
         */
        public byte[] FrameBuffer { get; set; }
    }
}
