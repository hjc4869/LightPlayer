using System.Reflection;
using LightStudio.NativeBindings;

namespace LightStudio.FfmpegShim;

[Include("errno.h", Access = BindingFlags.Public, Macros = ["EAGAIN", "EIO", "ENOMEM"])]
[Include("libavcodec/avcodec.h", Access = BindingFlags.Public)]
[Include("libavcodec/bsf.h", Access = BindingFlags.Public)]
[Include("libavcodec/packet.h", Access = BindingFlags.Public)]
[Include("libavfilter/avfilter.h", Access = BindingFlags.Public)]
[Include("libavfilter/buffersink.h", Access = BindingFlags.Public)]
[Include("libavfilter/buffersrc.h", Access = BindingFlags.Public)]
[Include("libavformat/avformat.h", Access = BindingFlags.Public)]
[Include("libavutil/dict.h", Access = BindingFlags.Public)]
[Include("libavutil/display.h", Access = BindingFlags.Public)]
[Include("libavutil/dovi_meta.h", Access = BindingFlags.Public)]
[Include("libavutil/frame.h", Access = BindingFlags.Public)]
[Include("libavutil/hdr_dynamic_metadata.h", Access = BindingFlags.Public)]
[Include("libavutil/hwcontext.h", Access = BindingFlags.Public)]
[Include("libavutil/hwcontext_mediacodec.h", Access = BindingFlags.Public)]
#if WINDOWS
[Include("libavutil/hwcontext_d3d11va.h", Access = BindingFlags.Public)]
#endif
[Include("libavutil/imgutils.h", Access = BindingFlags.Public)]
[Include("libavutil/log.h", Access = BindingFlags.Public)]
[Include("libavutil/mastering_display_metadata.h", Access = BindingFlags.Public)]
[Include("libavutil/motion_vector.h", Access = BindingFlags.Public)]
[Include("libavutil/opt.h", Access = BindingFlags.Public)]
[Include("libavutil/pixdesc.h", Access = BindingFlags.Public)]
[Include("libswresample/swresample.h", Access = BindingFlags.Public)]
[Include("libswscale/swscale.h", Access = BindingFlags.Public)]
public static partial class Ffmpeg;