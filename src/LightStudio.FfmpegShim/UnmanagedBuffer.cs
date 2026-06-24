using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace LightStudio.FfmpegShim
{
    public unsafe struct UnmanagedBuffer : IDisposable
    {
        public byte* Content;
        public int Length;
        public static UnmanagedBuffer Allocate(int size)
        {
            UnmanagedBuffer buffer;
            buffer.Content = (byte*)Marshal.AllocHGlobal(size);
            buffer.Length = size;
            return buffer;
        }
        public void Dispose()
        {
            Marshal.FreeHGlobal((IntPtr)Content);
            Content = null;
            Length = 0;
        }
    }
}
