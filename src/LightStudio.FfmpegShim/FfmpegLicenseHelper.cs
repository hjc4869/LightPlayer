using LightStudio.NativeBindings.Types;
using System;
using System.Collections.Generic;
using System.Text;
using static LightStudio.FfmpegShim.Ffmpeg;


namespace LightStudio.FfmpegShim
{
    /// <summary>
    /// Queries the loaded FFmpeg libraries for their license, build configuration and the set of
    /// components (muxers, demuxers, decoders, encoders, filters) compiled into them, so the About
    /// screens can display the notices required by the LGPL/GPL.
    /// </summary>
    public static unsafe partial class FfmpegLicenseHelper
    {
        /// <summary>Returns the FFmpeg license header followed by the component listings.</summary>
        /// <remarks>Throws if the native FFmpeg libraries cannot be loaded; callers must handle that.</remarks>
        public static string BuildNoticeText()
        {
            var builder = new StringBuilder();
            builder.Append("FFmpeg");
            builder.Append('\n');
            builder.Append("License: ").Append(avutil_license()).Append('\n');
            builder.Append("Libraries: ")
                   .Append("libavutil ").Append(FormatVersion(avutil_version()))
                   .Append(", libavcodec ").Append(FormatVersion(avcodec_version()))
                   .Append(", libavformat ").Append(FormatVersion(avformat_version()))
                   .Append(", libavfilter ").Append(FormatVersion(avfilter_version()))
                   .Append('\n');
            builder.Append("Configuration: ").Append(avutil_configuration()).Append('\n');

            AppendSection(builder, "Demuxers", EnumerateDemuxers());
            AppendSection(builder, "Muxers", EnumerateMuxers());
            AppendSection(builder, "Decoders", EnumerateCodecs(encoders: false));
            AppendSection(builder, "Encoders", EnumerateCodecs(encoders: true));
            AppendSection(builder, "Filters", EnumerateFilters());

            return builder.ToString();
        }

        private static void AppendSection(StringBuilder builder, string title, List<string> names)
        {
            names.Sort(StringComparer.OrdinalIgnoreCase);
            builder.Append('\n');
            builder.Append(title).Append(" (").Append(names.Count).Append("):\n");
            builder.Append(names.Count == 0 ? "(none)" : string.Join(", ", names));
            builder.Append('\n');
        }

        private static List<string> EnumerateDemuxers()
        {
            var names = new List<string>();
            void* opaque = null;
            AVInputFormat* format;
            while ((format = av_demuxer_iterate(&opaque)) != null)
            {
                AddName(names, format->name);
            }

            return names;
        }

        private static List<string> EnumerateMuxers()
        {
            var names = new List<string>();
            void* opaque = null;
            AVOutputFormat* format;
            while ((format = av_muxer_iterate(&opaque)) != null)
            {
                AddName(names, format->name);
            }

            return names;
        }

        private static List<string> EnumerateCodecs(bool encoders)
        {
            var names = new List<string>();
            void* opaque = null;
            AVCodec* codec;
            while ((codec = av_codec_iterate(&opaque)) != null)
            {
                var matches = encoders ? av_codec_is_encoder(codec) : av_codec_is_decoder(codec);
                if (matches != 0)
                {
                    AddName(names, codec->name);
                }
            }

            return names;
        }

        private static List<string> EnumerateFilters()
        {
            var names = new List<string>();
            void* opaque = null;
            AVFilter* filter;
            while ((filter = av_filter_iterate(&opaque)) != null)
            {
                AddName(names, filter->name);
            }

            return names;
        }

        private static void AddName(List<string> names, byte* name)
        {
            if (name == null)
            {
                return;
            }

            var value = Utils.NullTerminatedUTF8StringToString((sbyte*)name);
            if (!string.IsNullOrEmpty(value))
            {
                names.Add(value);
            }
        }

        private static string FormatVersion(uint version) =>
            $"{version >> 16}.{(version >> 8) & 0xFF}.{version & 0xFF}";
    }
}
