using System;

namespace LightStudio.FfmpegShim
{
    public readonly record struct AudioFormat(uint SampleRate, uint ChannelCount, uint BitsPerSample)
    {
        public int BytesPerSample => checked((int)((BitsPerSample + 7) / 8));

        public int BlockAlign => checked((int)ChannelCount * BytesPerSample);

        public int ByteRate => checked((int)SampleRate * BlockAlign);

        public static AudioFormat FromPcmEncodingProperties(PcmEncodingProperties properties)
        {
            return new AudioFormat(properties.SampleRate, properties.ChannelCount, properties.BitsPerSample);
        }

        public PcmEncodingProperties ToPcmEncodingProperties()
        {
            return new PcmEncodingProperties
            {
                SampleRate = SampleRate,
                ChannelCount = ChannelCount,
                BitsPerSample = BitsPerSample
            };
        }

        public TimeSpan GetDuration(long byteCount)
        {
            return ByteRate == 0
                ? TimeSpan.Zero
                : TimeSpan.FromTicks(byteCount * TimeSpan.TicksPerSecond / ByteRate);
        }

        public int AlignByteCount(int byteCount)
        {
            return BlockAlign == 0
                ? byteCount
                : byteCount - byteCount % BlockAlign;
        }
    }
}