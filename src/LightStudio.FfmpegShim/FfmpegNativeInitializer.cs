using FFmpeg.AutoGen;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace LightStudio.FfmpegShim
{
    public static class FfmpegNativeInitializer
    {
        private static int initialized;
        private static string rootPath;

        public static string RootPath
        {
            get
            {
                Initialize();
                return rootPath;
            }
        }

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref initialized, 1) == 1)
            {
                return;
            }

            rootPath = InitializeLinuxRootPath();
        }

        public static unsafe string GetErrorMessage(int error)
        {
            Initialize();
            var buffer = stackalloc byte[256];
            return ffmpeg.av_strerror(error, buffer, 256) < 0
                ? $"Unknown FFmpeg error {error}"
                : Utils.NullTerminatedUTF8StringToString((sbyte*)buffer);
        }

        private static string InitializeLinuxRootPath()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return null;
            }

            var environmentRoot = Environment.GetEnvironmentVariable("FFMPEG_ROOT");
            if (ContainsFfmpegLibraries(environmentRoot))
            {
                ffmpeg.RootPath = Path.GetFullPath(environmentRoot);
                return ffmpeg.RootPath;
            }

            var candidates = new[]
            {
                "/usr/lib/x86_64-linux-gnu",
                "/lib/x86_64-linux-gnu",
                "/usr/local/lib",
                "/usr/lib64",
                "/usr/lib"
            };

            var root = candidates.FirstOrDefault(ContainsFfmpegLibraries);
            if (!string.IsNullOrWhiteSpace(root))
            {
                ffmpeg.RootPath = root;
            }

            return root;
        }

        private static bool ContainsFfmpegLibraries(string path)
        {
            return !string.IsNullOrWhiteSpace(path) &&
                Directory.Exists(path) &&
                Directory.EnumerateFiles(path, "libavformat.so*").Any() &&
                Directory.EnumerateFiles(path, "libavcodec.so*").Any() &&
                Directory.EnumerateFiles(path, "libavutil.so*").Any();
        }
    }
}