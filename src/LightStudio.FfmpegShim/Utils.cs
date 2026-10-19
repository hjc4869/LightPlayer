using System;
using System.Text;
using LightStudio.NativeBindings.Types;

namespace LightStudio.FfmpegShim
{
    unsafe class Utils
    {
        public static double RationalToDouble(AVRational value) => value.num / (double)value.den;

        public static AVRational InvertRational(AVRational value) => new() { num = value.den, den = value.num };

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
