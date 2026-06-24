using System;
using System.Text;

namespace LightStudio.FfmpegShim
{
    unsafe class Utils
    {
        public static void* memcpy(void* destination, void* source, int num)
        {
            Buffer.MemoryCopy(source, destination, num, num);
            return destination;
        }
        public static int NullTerminatedStringLength(sbyte* str)
        {
            int l = 0;
            for (;;)
            {
                if (str[l] == 0)
                    return l;
                else l++;
            }
        }
        public static string NullTerminatedUTF8StringToString(sbyte* str)
        {
            var length = NullTerminatedStringLength(str);
            return Encoding.UTF8.GetString((byte*)str, length);
        }
    }
}
