using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Android.Content;
using LightStudio.Logging;
using LightStudio.NativeBindings;
using LightStudio.NativeBindings.Types;

namespace LightStudio.LightPlayer.Android.Video;

/// <summary>
/// Native Android window and graphics entry points for decoder output.
/// </summary>
/// <remarks>
/// EGL and GLES functions are loaded from the system libraries at run time even where the image
/// links statically: the renderer's context belongs to that driver while the image also carries
/// ANGLE's implementation of the GLES entry points, and a device exports only the EGL extensions
/// it has, whatever the NDK stubs declare.
/// </remarks>
[Include("GLES3/gl3.h", Access = BindingFlags.Public, ImportName = "libGLESv2.so")]
[Include("GLES2/gl2ext.h", Access = BindingFlags.Public, ImportName = "libGLESv2.so", Prerequisites = ["GLES3/gl3.h"])]
[Include("EGL/egl.h", Access = BindingFlags.Public, ImportName = "libEGL.so")]
[Include("EGL/eglext.h", Access = BindingFlags.Public, ImportName = "libEGL.so", Prerequisites = ["EGL/egl.h"])]
[Include("android/hardware_buffer.h", Access = BindingFlags.Public)]
[Include("android/native_window_jni.h", Access = BindingFlags.Public)]
[Include("media/NdkImageReader.h", Access = BindingFlags.Public)]
internal static unsafe partial class AndroidVideoInterop
{
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
    private static delegate* unmanaged[Cdecl]<void*, void*, int> duplicateNativeFence;

#if FFMPEG_STATIC
    [LibraryImport("__Internal", EntryPoint = "av_jni_set_java_vm")]
    private static partial int SetJavaVirtualMachine(IntPtr virtualMachine, IntPtr logContext);

    [LibraryImport("__Internal", EntryPoint = "av_mediacodec_release_buffer")]
    private static partial int ReleaseMediaCodecBuffer(IntPtr buffer, int render);
#endif

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
        glGenTextures(1, &texture);
        if (texture == 0)
            return 0;

        glBindTexture(GL_TEXTURE_EXTERNAL_OES, texture);
        glTexParameteri(GL_TEXTURE_EXTERNAL_OES, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
        glTexParameteri(GL_TEXTURE_EXTERNAL_OES, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        glTexParameteri(GL_TEXTURE_EXTERNAL_OES, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        glTexParameteri(GL_TEXTURE_EXTERNAL_OES, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
        glBindTexture(GL_TEXTURE_EXTERNAL_OES, 0);
        return texture;
    }

    public static void DeleteTexture(uint texture)
    {
        if (texture != 0)
            glDeleteTextures(1, &texture);
    }

    /// <summary>Wraps a hardware buffer as an image of the display, or returns null.</summary>
    public static void* CreateImage(void* display, AHardwareBuffer* buffer)
    {
        int* attributes = stackalloc int[] { EGL_IMAGE_PRESERVED_KHR, EGL_TRUE, EGL_NONE };
        return eglCreateImageKHR(display, null, EGL_NATIVE_BUFFER_ANDROID, eglGetNativeClientBufferANDROID(buffer), attributes);
    }

    /// <summary>
    /// A fence the GPU signals once the commands issued so far have run, as a file descriptor a
    /// buffer queue can wait on before it hands the buffer out again, or -1 when none can be made.
    /// </summary>
    public static int CreateReleaseFence(void* display)
    {
        delegate* unmanaged[Cdecl]<void*, void*, int> duplicate = duplicateNativeFence;
        if (duplicate == null)
        {
            duplicate = (delegate* unmanaged[Cdecl]<void*, void*, int>)eglGetProcAddress("eglDupNativeFenceFDANDROID");
            if (duplicate == null)
                return EGL_NO_NATIVE_FENCE_FD_ANDROID;
            duplicateNativeFence = duplicate;
        }

        void* sync = eglCreateSyncKHR(display, EGL_SYNC_NATIVE_FENCE_ANDROID, null);
        if (sync == null)
            return EGL_NO_NATIVE_FENCE_FD_ANDROID;

        // The descriptor only exists once the sync has reached the GPU.
        glFlush();
        int fence = duplicate(display, sync);
        eglDestroySyncKHR(display, sync);
        return fence;
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

        string extensions = Marshal.PtrToStringUTF8((IntPtr)glGetString(GL_EXTENSIONS)) ?? string.Empty;
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
            glGenTextures(1, &texture);
            glBindTexture(GL_TEXTURE_2D, texture);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
            glTexImage2D(
                GL_TEXTURE_2D,
                0,
                GL_RGBA16F,
                width,
                height,
                0,
                GL_RGBA,
                GL_HALF_FLOAT,
                null);

            uint error = glGetError();
            if (texture == 0 || error != GL_NO_ERROR)
            {
                declined = texture == 0
                    ? "OpenGL would not allocate the F16 texture"
                    : $"allocating the F16 texture returned GL error 0x{error:X}";
                return false;
            }

            glGenFramebuffers(1, &framebuffer);
            glBindFramebuffer(GL_FRAMEBUFFER, framebuffer);
            glFramebufferTexture2D(
                GL_FRAMEBUFFER,
                GL_COLOR_ATTACHMENT0,
                GL_TEXTURE_2D,
                texture,
                0);
            uint status = glCheckFramebufferStatus(GL_FRAMEBUFFER);
            error = glGetError();
            if (framebuffer == 0 || status != GL_FRAMEBUFFER_COMPLETE || error != GL_NO_ERROR)
            {
                declined = framebuffer == 0
                    ? "OpenGL would not allocate the F16 framebuffer"
                    : status != GL_FRAMEBUFFER_COMPLETE
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
                    glDeleteFramebuffers(1, &framebuffer);
                if (texture != 0)
                    glDeleteTextures(1, &texture);
                glDeleteProgram(program);
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
        glBindFramebuffer(GL_FRAMEBUFFER, copy.Framebuffer);
        glViewport(0, 0, width, height);
        glDisable(GL_BLEND);
        glDisable(GL_CULL_FACE);
        glDisable(GL_DEPTH_TEST);
        glDisable(GL_DITHER);
        glDisable(GL_SCISSOR_TEST);
        glDisable(GL_STENCIL_TEST);
        glDisable(GL_RASTERIZER_DISCARD);
        glColorMask(1, 1, 1, 1);
        glUseProgram(copy.Program);
        glActiveTexture(GL_TEXTURE0);
        glBindTexture(GL_TEXTURE_EXTERNAL_OES, sourceTexture);
        glUniform1i(copy.SamplerLocation, 0);
        glDrawArrays(GL_TRIANGLES, 0, 3);

        uint error = glGetError();
        declined = error == GL_NO_ERROR ? string.Empty : $"the YUV copy returned GL error 0x{error:X}";
        return error == GL_NO_ERROR;
    }

    public static void DeleteExternalYuvCopy(ExternalYuvCopy copy)
    {
        uint framebuffer = copy.Framebuffer;
        uint texture = copy.Texture;
        if (framebuffer != 0)
            glDeleteFramebuffers(1, &framebuffer);
        if (texture != 0)
            glDeleteTextures(1, &texture);
        if (copy.Program != 0)
            glDeleteProgram(copy.Program);
    }

    private static bool TryCreateYuvCopyProgram(
        out uint program,
        out int samplerLocation,
        out string declined)
    {
        program = 0;
        samplerLocation = -1;
        if (!TryCompileShader(GL_VERTEX_SHADER, YuvCopyVertexShader, out uint vertex, out declined))
            return false;

        if (!TryCompileShader(GL_FRAGMENT_SHADER, YuvCopyFragmentShader, out uint fragment, out declined))
        {
            glDeleteShader(vertex);
            return false;
        }

        program = glCreateProgram();
        if (program != 0)
        {
            glAttachShader(program, vertex);
            glAttachShader(program, fragment);
            glLinkProgram(program);
        }
        glDeleteShader(vertex);
        glDeleteShader(fragment);

        int linked = 0;
        if (program != 0)
            glGetProgramiv(program, GL_LINK_STATUS, &linked);
        if (linked == 0)
        {
            declined = program == 0
                ? "OpenGL would not create the YUV copy program"
                : $"the YUV copy program did not link: {ReadProgramLog(program)}";
            if (program != 0)
            {
                glDeleteProgram(program);
                program = 0;
            }
            return false;
        }

        samplerLocation = glGetUniformLocation(program, "sourceTexture");
        if (samplerLocation >= 0)
        {
            declined = string.Empty;
            return true;
        }

        declined = "the YUV copy program omitted its source sampler";
        glDeleteProgram(program);
        program = 0;
        return false;
    }

    private static bool TryCompileShader(
        uint type,
        string source,
        out uint shader,
        out string declined)
    {
        shader = glCreateShader(type);
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
            glShaderSource(shader, 1, &sourcePointer, &length);
            glCompileShader(shader);
        }
        finally
        {
            Marshal.FreeCoTaskMem(allocation);
        }

        int compiled = 0;
        glGetShaderiv(shader, GL_COMPILE_STATUS, &compiled);
        if (compiled != 0)
        {
            declined = string.Empty;
            return true;
        }

        declined = $"a YUV copy shader did not compile: {ReadShaderLog(shader)}";
        glDeleteShader(shader);
        shader = 0;
        return false;
    }

    private static string ReadShaderLog(uint shader)
    {
        int length = 0;
        glGetShaderiv(shader, GL_INFO_LOG_LENGTH, &length);
        if (length <= 1)
            return "no compiler log";

        var bytes = new byte[length];
        fixed (byte* buffer = bytes)
        {
            int written = 0;
            glGetShaderInfoLog(shader, bytes.Length, &written, buffer);
            return Encoding.UTF8.GetString(bytes, 0, Math.Clamp(written, 0, bytes.Length)).TrimEnd('\0');
        }
    }

    private static string ReadProgramLog(uint program)
    {
        int length = 0;
        glGetProgramiv(program, GL_INFO_LOG_LENGTH, &length);
        if (length <= 1)
            return "no linker log";

        var bytes = new byte[length];
        fixed (byte* buffer = bytes)
        {
            int written = 0;
            glGetProgramInfoLog(program, bytes.Length, &written, buffer);
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
        while (glGetError() != GL_NO_ERROR)
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
