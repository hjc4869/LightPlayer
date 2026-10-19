using System;

namespace LightStudio.FfmpegShim
{
    /// <summary>
    /// Helpers for the app's internal packed 24-bit little-endian PCM (3 bytes per sample). The
    /// decoder resamples to this format end-to-end; backends that cannot hand 24-bit PCM to the
    /// operating system narrow it to 16-bit here at their output boundary.
    /// </summary>
    public static class PcmConvert
    {
        /// <summary>Bytes occupied by a single packed 24-bit PCM sample.</summary>
        public const int Bytes24 = 3;

        /// <summary>
        /// Narrows packed 24-bit little-endian PCM to 16-bit little-endian by keeping each sample's
        /// two most-significant bytes (truncation). Returns the number of bytes written to
        /// <paramref name="destination"/>, which must hold at least two thirds of the sample-aligned
        /// source length.
        /// </summary>
        public static int PackedS24ToS16(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            var sampleCount = source.Length / Bytes24;
            for (var i = 0; i < sampleCount; i++)
            {
                var src = i * Bytes24;
                // Drop the least-significant byte (index 0) of the little-endian sample.
                destination[i * 2] = source[src + 1];
                destination[i * 2 + 1] = source[src + 2];
            }

            return sampleCount * 2;
        }

        /// <summary>
        /// Scales packed 24-bit little-endian PCM in place by <paramref name="level"/> (clamped to
        /// 0..1). A level of 1 leaves the samples untouched; a level of 0 zeroes them.
        /// </summary>
        public static void ApplyVolumeS24(Span<byte> pcm, float level)
        {
            if (level >= 1.0f)
            {
                return;
            }

            if (level <= 0.0f)
            {
                pcm.Clear();
                return;
            }

            var sampleCount = pcm.Length / Bytes24;
            for (var i = 0; i < sampleCount; i++)
            {
                var offset = i * Bytes24;
                var sample = pcm[offset] | (pcm[offset + 1] << 8) | (pcm[offset + 2] << 16);
                // Sign-extend the 24-bit value into the full 32-bit integer before scaling.
                if ((sample & 0x0080_0000) != 0)
                {
                    sample |= unchecked((int)0xFF00_0000);
                }

                sample = (int)(sample * level);
                pcm[offset] = (byte)sample;
                pcm[offset + 1] = (byte)(sample >> 8);
                pcm[offset + 2] = (byte)(sample >> 16);
            }
        }
    }
}
