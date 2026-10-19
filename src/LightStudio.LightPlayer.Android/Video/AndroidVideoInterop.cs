using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Android.Content;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Android.Video;

/// <summary>
/// Native Android window and graphics entry points for decoder output.
/// </summary>
internal static unsafe partial class AndroidVideoInterop
{
    public const uint GlTextureExternalOes = 0x8D65;
    public const uint GlTexture2D = 0x0DE1;

    private const uint GlExtensions = 0x1F03;
    private const uint GlTextureMinFilter = 0x2801;
    private const uint GlTextureMagFilter = 0x2800;
    private const uint GlTextureWrapS = 0x2802;
    private const uint GlTextureWrapT = 0x2803;
    private const uint GlLinear = 0x2601;
    private const uint GlClampToEdge = 0x812F;
    private const uint GlRgba = 0x1908;
    private const uint GlRgba16Float = 0x881A;
    private const uint GlHalfFloat = 0x140B;
    private const uint GlFramebuffer = 0x8D40;
    private const uint GlColorAttachment0 = 0x8CE0;
    private const uint GlFramebufferComplete = 0x8CD5;
    private const uint GlVertexShader = 0x8B31;
    private const uint GlFragmentShader = 0x8B30;
    private const uint GlCompileStatus = 0x8B81;
    private const uint GlLinkStatus = 0x8B82;
    private const uint GlInfoLogLength = 0x8B84;
    private const uint GlTexture0 = 0x84C0;
    private const uint GlTriangles = 0x0004;
    private const uint GlBlend = 0x0BE2;
    private const uint GlCullFace = 0x0B44;
    private const uint GlDepthTest = 0x0B71;
    private const uint GlDither = 0x0BD0;
    private const uint GlScissorTest = 0x0C11;
    private const uint GlStencilTest = 0x0B90;
    private const uint GlRasterizerDiscard = 0x8C89;
    private const uint GlNoError = 0;

    private const string YuvCopyVertexShader = """
        #version 300 es
        precision highp float;

        out vec2 textureCoordinate;

        void main() {
            vec2 position = gl_VertexID == 0
                ? vec2(-1.0, -1.0)
                : gl_VertexID == 1
                    ? vec2(3.0, -1.0)
                    : vec2(-1.0, 3.0);
            textureCoordinate = position * 0.5 + 0.5;
            gl_Position = vec4(position, 0.0, 1.0);
        }
        """;

    private const string YuvCopyFragmentShader = """
        #version 300 es
        #extension GL_OES_EGL_image_external : require
        #extension GL_EXT_YUV_target : require
        precision highp float;
        precision highp __samplerExternal2DY2YEXT;

        uniform __samplerExternal2DY2YEXT sourceTexture;
        in vec2 textureCoordinate;
        out vec4 outputColor;

        void main() {
            outputColor = vec4(texture(sourceTexture, textureCoordinate).xyz, 1.0);
        }
        """;

    private static readonly object ffmpegSync = new();
#if !FFMPEG_STATIC
    private static IntPtr avcodec;
    private static delegate* unmanaged[Cdecl]<IntPtr, int, int> releaseBuffer;
#endif
    private static bool javaVmPublished;

#if FFMPEG_STATIC
    [LibraryImport("__Internal", EntryPoint = "av_jni_set_java_vm")]
    private static partial int SetJavaVirtualMachine(IntPtr virtualMachine, IntPtr logContext);

    [LibraryImport("__Internal", EntryPoint = "av_mediacodec_release_buffer")]
    private static partial int ReleaseMediaCodecBuffer(IntPtr buffer, int render);
#endif

    [LibraryImport("android", EntryPoint = "ANativeWindow_fromSurface")]
    public static partial IntPtr NativeWindowFromSurface(IntPtr env, IntPtr surface);

    [LibraryImport("android", EntryPoint = "ANativeWindow_release")]
    public static partial void NativeWindowRelease(IntPtr window);

    [LibraryImport("GLESv2", EntryPoint = "glGenTextures")]
    private static partial void GlGenTextures(int count, uint* textures);

    [LibraryImport("GLESv2", EntryPoint = "glDeleteTextures")]
    private static partial void GlDeleteTextures(int count, uint* textures);

    [LibraryImport("GLESv2", EntryPoint = "glBindTexture")]
    private static partial void GlBindTexture(uint target, uint texture);

    [LibraryImport("GLESv2", EntryPoint = "glTexParameteri")]
    private static partial void GlTexParameteri(uint target, uint parameter, int value);

    [LibraryImport("GLESv2", EntryPoint = "glGetString")]
    private static partial IntPtr GlGetString(uint name);

    [LibraryImport("GLESv2", EntryPoint = "glGetError")]
    private static partial uint GlGetError();

    [LibraryImport("GLESv2", EntryPoint = "glTexImage2D")]
    private static partial void GlTexImage2D(
        uint target,
        int level,
        int internalFormat,
        int width,
        int height,
        int border,
        uint format,
        uint type,
        void* pixels);

    [LibraryImport("GLESv2", EntryPoint = "glGenFramebuffers")]
    private static partial void GlGenFramebuffers(int count, uint* framebuffers);

    [LibraryImport("GLESv2", EntryPoint = "glDeleteFramebuffers")]
    private static partial void GlDeleteFramebuffers(int count, uint* framebuffers);

    [LibraryImport("GLESv2", EntryPoint = "glBindFramebuffer")]
    private static partial void GlBindFramebuffer(uint target, uint framebuffer);

    [LibraryImport("GLESv2", EntryPoint = "glFramebufferTexture2D")]
    private static partial void GlFramebufferTexture2D(
        uint target,
        uint attachment,
        uint textureTarget,
        uint texture,
        int level);

    [LibraryImport("GLESv2", EntryPoint = "glCheckFramebufferStatus")]
    private static partial uint GlCheckFramebufferStatus(uint target);

    [LibraryImport("GLESv2", EntryPoint = "glCreateShader")]
    private static partial uint GlCreateShader(uint type);

    [LibraryImport("GLESv2", EntryPoint = "glShaderSource")]
    private static partial void GlShaderSource(uint shader, int count, byte** source, int* length);

    [LibraryImport("GLESv2", EntryPoint = "glCompileShader")]
    private static partial void GlCompileShader(uint shader);

    [LibraryImport("GLESv2", EntryPoint = "glGetShaderiv")]
    private static partial void GlGetShaderiv(uint shader, uint parameter, int* value);

    [LibraryImport("GLESv2", EntryPoint = "glGetShaderInfoLog")]
    private static partial void GlGetShaderInfoLog(uint shader, int capacity, int* length, byte* log);

    [LibraryImport("GLESv2", EntryPoint = "glDeleteShader")]
    private static partial void GlDeleteShader(uint shader);

    [LibraryImport("GLESv2", EntryPoint = "glCreateProgram")]
    private static partial uint GlCreateProgram();

    [LibraryImport("GLESv2", EntryPoint = "glAttachShader")]
    private static partial void GlAttachShader(uint program, uint shader);

    [LibraryImport("GLESv2", EntryPoint = "glLinkProgram")]
    private static partial void GlLinkProgram(uint program);

    [LibraryImport("GLESv2", EntryPoint = "glGetProgramiv")]
    private static partial void GlGetProgramiv(uint program, uint parameter, int* value);

    [LibraryImport("GLESv2", EntryPoint = "glGetProgramInfoLog")]
    private static partial void GlGetProgramInfoLog(uint program, int capacity, int* length, byte* log);

    [LibraryImport("GLESv2", EntryPoint = "glDeleteProgram")]
    private static partial void GlDeleteProgram(uint program);

    [LibraryImport("GLESv2", EntryPoint = "glGetUniformLocation")]
    private static partial int GlGetUniformLocation(uint program, byte* name);

    [LibraryImport("GLESv2", EntryPoint = "glViewport")]
    private static partial void GlViewport(int x, int y, int width, int height);

    [LibraryImport("GLESv2", EntryPoint = "glDisable")]
    private static partial void GlDisable(uint capability);

    [LibraryImport("GLESv2", EntryPoint = "glColorMask")]
    private static partial void GlColorMask(byte red, byte green, byte blue, byte alpha);

    [LibraryImport("GLESv2", EntryPoint = "glUseProgram")]
    private static partial void GlUseProgram(uint program);

    [LibraryImport("GLESv2", EntryPoint = "glActiveTexture")]
    private static partial void GlActiveTexture(uint texture);

    [LibraryImport("GLESv2", EntryPoint = "glUniform1i")]
    private static partial void GlUniform1i(int location, int value);

    [LibraryImport("GLESv2", EntryPoint = "glDrawArrays")]
    private static partial void GlDrawArrays(uint mode, int first, int count);

    public readonly record struct ExternalYuvCopy(
        uint Texture,
        uint Framebuffer,
        uint Program,
        int SamplerLocation)
    {
        public bool IsValid => Texture != 0 && Framebuffer != 0 && Program != 0;
    }

    /// <summary>
    /// Makes one external texture and gives it the only sampling an external target admits:
    /// clamped, unfiltered between mip levels, because it has none.
    /// </summary>
    public static uint CreateExternalTexture()
    {
        uint texture = 0;
        GlGenTextures(1, &texture);
        if (texture == 0)
            return 0;

        GlBindTexture(GlTextureExternalOes, texture);
        GlTexParameteri(GlTextureExternalOes, GlTextureMinFilter, (int)GlLinear);
        GlTexParameteri(GlTextureExternalOes, GlTextureMagFilter, (int)GlLinear);
        GlTexParameteri(GlTextureExternalOes, GlTextureWrapS, (int)GlClampToEdge);
        GlTexParameteri(GlTextureExternalOes, GlTextureWrapT, (int)GlClampToEdge);
        GlBindTexture(GlTextureExternalOes, 0);
        return texture;
    }

    public static void DeleteTexture(uint texture)
    {
        if (texture != 0)
            GlDeleteTextures(1, &texture);
    }

    public static bool TryCreateExternalYuvCopy(
        int width,
        int height,
        out ExternalYuvCopy copy,
        out string declined)
    {
        copy = default;
        declined = string.Empty;
        if (width <= 0 || height <= 0)
        {
            declined = "the coded size is empty";
            return false;
        }

        string extensions = Marshal.PtrToStringAnsi(GlGetString(GlExtensions)) ?? string.Empty;
        if (!HasExtension(extensions, "GL_EXT_YUV_target"))
        {
            declined = "GL_EXT_YUV_target is not supported";
            return false;
        }

        ClearGlErrors();
        if (!TryCreateYuvCopyProgram(out uint program, out int samplerLocation, out declined))
            return false;

        uint texture = 0;
        uint framebuffer = 0;
        bool succeeded = false;
        try
        {
            GlGenTextures(1, &texture);
            GlBindTexture(GlTexture2D, texture);
            GlTexParameteri(GlTexture2D, GlTextureMinFilter, (int)GlLinear);
            GlTexParameteri(GlTexture2D, GlTextureMagFilter, (int)GlLinear);
            GlTexParameteri(GlTexture2D, GlTextureWrapS, (int)GlClampToEdge);
            GlTexParameteri(GlTexture2D, GlTextureWrapT, (int)GlClampToEdge);
            GlTexImage2D(
                GlTexture2D,
                0,
                (int)GlRgba16Float,
                width,
                height,
                0,
                GlRgba,
                GlHalfFloat,
                null);

            uint error = GlGetError();
            if (texture == 0 || error != GlNoError)
            {
                declined = texture == 0
                    ? "OpenGL would not allocate the F16 texture"
                    : $"allocating the F16 texture returned GL error 0x{error:X}";
                return false;
            }

            GlGenFramebuffers(1, &framebuffer);
            GlBindFramebuffer(GlFramebuffer, framebuffer);
            GlFramebufferTexture2D(
                GlFramebuffer,
                GlColorAttachment0,
                GlTexture2D,
                texture,
                0);
            uint status = GlCheckFramebufferStatus(GlFramebuffer);
            error = GlGetError();
            if (framebuffer == 0 || status != GlFramebufferComplete || error != GlNoError)
            {
                declined = framebuffer == 0
                    ? "OpenGL would not allocate the F16 framebuffer"
                    : status != GlFramebufferComplete
                        ? $"the F16 framebuffer is incomplete (0x{status:X})"
                        : $"configuring the F16 framebuffer returned GL error 0x{error:X}";
                return false;
            }

            copy = new ExternalYuvCopy(texture, framebuffer, program, samplerLocation);
            succeeded = true;
            return true;
        }
        finally
        {
            if (!succeeded)
            {
                if (framebuffer != 0)
                    GlDeleteFramebuffers(1, &framebuffer);
                if (texture != 0)
                    GlDeleteTextures(1, &texture);
                GlDeleteProgram(program);
            }
        }
    }

    public static bool CopyExternalYuv(
        uint sourceTexture,
        ExternalYuvCopy copy,
        int width,
        int height,
        out string declined)
    {
        if (sourceTexture == 0 || !copy.IsValid)
        {
            declined = "the YUV copy resources are incomplete";
            return false;
        }

        ClearGlErrors();
        GlBindFramebuffer(GlFramebuffer, copy.Framebuffer);
        GlViewport(0, 0, width, height);
        GlDisable(GlBlend);
        GlDisable(GlCullFace);
        GlDisable(GlDepthTest);
        GlDisable(GlDither);
        GlDisable(GlScissorTest);
        GlDisable(GlStencilTest);
        GlDisable(GlRasterizerDiscard);
        GlColorMask(1, 1, 1, 1);
        GlUseProgram(copy.Program);
        GlActiveTexture(GlTexture0);
        GlBindTexture(GlTextureExternalOes, sourceTexture);
        GlUniform1i(copy.SamplerLocation, 0);
        GlDrawArrays(GlTriangles, 0, 3);

        uint error = GlGetError();
        declined = error == GlNoError ? string.Empty : $"the YUV copy returned GL error 0x{error:X}";
        return error == GlNoError;
    }

    public static void DeleteExternalYuvCopy(ExternalYuvCopy copy)
    {
        uint framebuffer = copy.Framebuffer;
        uint texture = copy.Texture;
        if (framebuffer != 0)
            GlDeleteFramebuffers(1, &framebuffer);
        if (texture != 0)
            GlDeleteTextures(1, &texture);
        if (copy.Program != 0)
            GlDeleteProgram(copy.Program);
    }

    private static bool TryCreateYuvCopyProgram(
        out uint program,
        out int samplerLocation,
        out string declined)
    {
        program = 0;
        samplerLocation = -1;
        if (!TryCompileShader(GlVertexShader, YuvCopyVertexShader, out uint vertex, out declined))
            return false;

        if (!TryCompileShader(GlFragmentShader, YuvCopyFragmentShader, out uint fragment, out declined))
        {
            GlDeleteShader(vertex);
            return false;
        }

        program = GlCreateProgram();
        if (program != 0)
        {
            GlAttachShader(program, vertex);
            GlAttachShader(program, fragment);
            GlLinkProgram(program);
        }
        GlDeleteShader(vertex);
        GlDeleteShader(fragment);

        int linked = 0;
        if (program != 0)
            GlGetProgramiv(program, GlLinkStatus, &linked);
        if (linked == 0)
        {
            declined = program == 0
                ? "OpenGL would not create the YUV copy program"
                : $"the YUV copy program did not link: {ReadProgramLog(program)}";
            if (program != 0)
            {
                GlDeleteProgram(program);
                program = 0;
            }
            return false;
        }

        samplerLocation = GetUniformLocation(program, "sourceTexture");
        if (samplerLocation >= 0)
        {
            declined = string.Empty;
            return true;
        }

        declined = "the YUV copy program omitted its source sampler";
        GlDeleteProgram(program);
        program = 0;
        return false;
    }

    private static bool TryCompileShader(
        uint type,
        string source,
        out uint shader,
        out string declined)
    {
        shader = GlCreateShader(type);
        if (shader == 0)
        {
            declined = "OpenGL would not create a YUV copy shader";
            return false;
        }

        IntPtr allocation = Marshal.StringToCoTaskMemUTF8(source);
        try
        {
            byte* sourcePointer = (byte*)allocation;
            int length = Encoding.UTF8.GetByteCount(source);
            GlShaderSource(shader, 1, &sourcePointer, &length);
            GlCompileShader(shader);
        }
        finally
        {
            Marshal.FreeCoTaskMem(allocation);
        }

        int compiled = 0;
        GlGetShaderiv(shader, GlCompileStatus, &compiled);
        if (compiled != 0)
        {
            declined = string.Empty;
            return true;
        }

        declined = $"a YUV copy shader did not compile: {ReadShaderLog(shader)}";
        GlDeleteShader(shader);
        shader = 0;
        return false;
    }

    private static int GetUniformLocation(uint program, string name)
    {
        IntPtr allocation = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            return GlGetUniformLocation(program, (byte*)allocation);
        }
        finally
        {
            Marshal.FreeCoTaskMem(allocation);
        }
    }

    private static string ReadShaderLog(uint shader)
    {
        int length = 0;
        GlGetShaderiv(shader, GlInfoLogLength, &length);
        if (length <= 1)
            return "no compiler log";

        var bytes = new byte[length];
        fixed (byte* buffer = bytes)
        {
            int written = 0;
            GlGetShaderInfoLog(shader, bytes.Length, &written, buffer);
            return Encoding.UTF8.GetString(bytes, 0, Math.Clamp(written, 0, bytes.Length)).TrimEnd('\0');
        }
    }

    private static string ReadProgramLog(uint program)
    {
        int length = 0;
        GlGetProgramiv(program, GlInfoLogLength, &length);
        if (length <= 1)
            return "no linker log";

        var bytes = new byte[length];
        fixed (byte* buffer = bytes)
        {
            int written = 0;
            GlGetProgramInfoLog(program, bytes.Length, &written, buffer);
            return Encoding.UTF8.GetString(bytes, 0, Math.Clamp(written, 0, bytes.Length)).TrimEnd('\0');
        }
    }

    private static bool HasExtension(string extensions, string extension)
    {
        int index = extensions.IndexOf(extension, StringComparison.Ordinal);
        while (index >= 0)
        {
            int end = index + extension.Length;
            bool startsAtBoundary = index == 0 || extensions[index - 1] == ' ';
            bool endsAtBoundary = end == extensions.Length || extensions[end] == ' ';
            if (startsAtBoundary && endsAtBoundary)
                return true;

            index = extensions.IndexOf(extension, end, StringComparison.Ordinal);
        }

        return false;
    }

    private static void ClearGlErrors()
    {
        while (GlGetError() != GlNoError)
        {
        }
    }

    /// <summary>
    /// Tells FFmpeg which virtual machine it is running in, so its MediaCodec decoders can use the
    /// Java API. Without this they fall back to the NDK one, which cannot pick a decoder by profile
    /// and so may land on a software codec for a stream the hardware would have taken.
    /// </summary>
    public static void PublishJavaVirtualMachine(Context context)
    {
        lock (ffmpegSync)
        {
            if (javaVmPublished)
                return;

            javaVmPublished = true;
            try
            {
#if !FFMPEG_STATIC
                IntPtr library = LoadAvcodec(context);
                if (library == IntPtr.Zero ||
                    !NativeLibrary.TryGetExport(library, "av_jni_set_java_vm", out IntPtr entry))
                {
                    return;
                }
#endif

                IntPtr virtualMachine = Java.Interop.JniRuntime.CurrentRuntime.InvocationPointer;
                if (virtualMachine == IntPtr.Zero)
                    return;

#if FFMPEG_STATIC
                int result = SetJavaVirtualMachine(virtualMachine, IntPtr.Zero);
#else
                var set = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)entry;
                int result = set(virtualMachine, IntPtr.Zero);
#endif
                AppLog.Information("Android.Video", $"Published the Java VM to FFmpeg; result={result}.");
            }
            catch (Exception exception)
            {
                AppLog.Warning("Android.Video", "Could not publish the Java VM to FFmpeg.", exception);
            }
        }
    }

    /// <summary>
    /// Hands one MediaCodec output buffer back, drawing it into the window it was configured with.
    /// </summary>
    public static bool RenderBuffer(Context context, IntPtr buffer)
    {
        if (buffer == IntPtr.Zero)
            return false;

    #if FFMPEG_STATIC
        return ReleaseMediaCodecBuffer(buffer, 1) >= 0;
    #else
        delegate* unmanaged[Cdecl]<IntPtr, int, int> release = releaseBuffer;
        if (release == null)
        {
            lock (ffmpegSync)
            {
                if (releaseBuffer == null)
                {
                    IntPtr library = LoadAvcodec(context);
                    releaseBuffer = library != IntPtr.Zero &&
                        NativeLibrary.TryGetExport(library, "av_mediacodec_release_buffer", out IntPtr entry)
                            ? (delegate* unmanaged[Cdecl]<IntPtr, int, int>)entry
                            : null;
                }

                release = releaseBuffer;
            }

            if (release == null)
                return false;
        }

        return release(buffer, 1) >= 0;
#endif
    }

    /// <summary>Whether the FFmpeg entry points this path needs are in the loaded build.</summary>
    public static bool SupportsBufferRendering(Context context)
    {
#if FFMPEG_STATIC
        return true;
#else
        lock (ffmpegSync)
        {
            IntPtr library = LoadAvcodec(context);
            return library != IntPtr.Zero &&
                NativeLibrary.TryGetExport(library, "av_mediacodec_release_buffer", out _);
        }
#endif
    }

#if !FFMPEG_STATIC
    private static IntPtr LoadAvcodec(Context context)
    {
        if (avcodec != IntPtr.Zero)
            return avcodec;

        // The same absolute path the decoder's own resolver uses, so this is the library already
        // open in the process rather than a second copy of it.
        string path = Path.Combine(context.ApplicationInfo!.NativeLibraryDir!, "libavcodec.so");
        if (NativeLibrary.TryLoad(path, out IntPtr handle) || NativeLibrary.TryLoad("libavcodec.so", out handle))
            avcodec = handle;

        return avcodec;
    }
#endif
}
