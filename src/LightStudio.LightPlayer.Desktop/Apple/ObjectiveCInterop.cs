using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace LightStudio.LightPlayer.Desktop.Apple;

/// <summary>
/// Minimal Objective-C runtime and Foundation/CoreFoundation binding surface used by the macOS
/// "now playing" integration (<see cref="AppleSystemMediaControls"/>). It exposes just enough of
/// the flat C runtime (<c>objc_msgSend</c>, class/selector lookup, dynamic class registration) plus
/// the CoreFoundation string helpers and the Blocks ABI to drive <c>MPNowPlayingInfoCenter</c> and
/// <c>MPRemoteCommandCenter</c> without a managed binding framework.
/// </summary>
/// <remarks>
/// <para>
/// <c>objc_msgSend</c> is declared in C as a variadic trampoline, but it must be called through a
/// prototype matching the target method's real (non-variadic) signature so arguments land in the
/// registers the method expects. This matters on Apple ARM64, where true variadic arguments are
/// passed on the stack. Each fixed-signature P/Invoke overload below therefore maps to a distinct
/// call shape; none is a genuine varargs call.
/// </para>
/// <para>
/// Only the 64-bit Apple ABIs (arm64 / x64, on macOS and iOS) are targeted. <c>CGSize</c> arguments are
/// spelled as two <see cref="double"/> parameters, which is register-layout-identical to passing the
/// struct by value on both architectures (an ARM64 homogeneous float aggregate / two x86-64 SSE
/// eightbytes).
/// </para>
/// </remarks>
internal static unsafe class ObjC
{
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private const string CoreFoundation =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    /// <summary>kCFStringEncodingUTF8.</summary>
    private const uint CFStringEncodingUTF8 = 0x08000100;

    // Blocks ABI (see Clang "Block Implementation Specification"). A block with no captured
    // variables is emitted as a global block: its isa is &_NSConcreteGlobalBlock and Block_copy /
    // Block_release are no-ops, so a single instance can be shared for the process lifetime.
    private const int BlockIsGlobal = 1 << 28;

    private static readonly ConcurrentDictionary<string, IntPtr> Selectors = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, IntPtr> Classes = new(StringComparer.Ordinal);

    private static readonly object BlockGate = new();
    private static IntPtr concreteGlobalBlock;
    private static IntPtr concreteStackBlock;
    private static IntPtr blockDescriptor;
    private static IntPtr contextBlockDescriptor;

    // ---- Objective-C runtime (flat C API) ------------------------------

    [DllImport(LibObjC)]
    private static extern IntPtr objc_getClass(byte* name);

    [DllImport(LibObjC)]
    private static extern IntPtr sel_registerName(byte* name);

    [DllImport(LibObjC)]
    private static extern IntPtr objc_allocateClassPair(IntPtr superclass, byte* name, nuint extraBytes);

    [DllImport(LibObjC)]
    private static extern void objc_registerClassPair(IntPtr cls);

    [DllImport(LibObjC)]
    private static extern byte class_addMethod(IntPtr cls, IntPtr name, IntPtr imp, byte* types);

    [DllImport(LibObjC)]
    private static extern IntPtr objc_autoreleasePoolPush();

    [DllImport(LibObjC)]
    private static extern void objc_autoreleasePoolPop(IntPtr pool);

    // objc_msgSend, one fixed-signature overload per call shape (see the type remarks above).
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg1);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr Send(IntPtr receiver, IntPtr selector, double arg1);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr Send(IntPtr receiver, IntPtr selector, byte arg1);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr Send(IntPtr receiver, IntPtr selector, double arg1, double arg2);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr Send(IntPtr receiver, IntPtr selector, double arg1, double arg2, IntPtr arg3);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2, IntPtr arg3);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2, IntPtr arg3, IntPtr arg4);

    // CGSize spelled as two doubles (see the type remarks above).
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr Send(
        IntPtr receiver, IntPtr selector, IntPtr arg1, double width, double height, IntPtr arg2, IntPtr arg3, IntPtr arg4);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg1, byte arg2);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern void Send(IntPtr receiver, IntPtr selector, CGRect rect, IntPtr arg);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern double SendDouble(IntPtr receiver, IntPtr selector);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern byte SendBool(IntPtr receiver, IntPtr selector);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern byte SendBool(IntPtr receiver, IntPtr selector, IntPtr arg1);

    // NSPoint spelled as two doubles (see the type remarks above).
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern byte SendBool(IntPtr receiver, IntPtr selector, IntPtr arg1, double x, double y, IntPtr arg2);

    // ---- CoreFoundation ------------------------------------------------

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, byte* cString, uint encoding);

    [DllImport(CoreFoundation)]
    public static extern void CFRelease(IntPtr cf);

    /// <summary>Objective-C <c>BOOL</c> value for <see langword="true"/>.</summary>
    public const byte Yes = 1;

    /// <summary>Objective-C <c>BOOL</c> value for <see langword="false"/>.</summary>
    public const byte No = 0;

    /// <summary><c>CGRect</c>/<c>NSRect</c>, passed by value with the platform's struct ABI.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CGRect(double x, double y, double width, double height)
    {
        public double X = x;
        public double Y = y;
        public double Width = width;
        public double Height = height;
    }

    /// <summary>Resolves (and caches) an Objective-C class by name; returns <see cref="IntPtr.Zero"/> if absent.</summary>
    public static IntPtr GetClass(string name) =>
        Classes.GetOrAdd(name, static n =>
        {
            Span<byte> buffer = stackalloc byte[256];
            var length = Encoding.ASCII.GetBytes(n, buffer);
            buffer[length] = 0;
            fixed (byte* p = buffer)
            {
                return objc_getClass(p);
            }
        });

    /// <summary>Registers (and caches) an Objective-C selector by name.</summary>
    public static IntPtr Sel(string name) =>
        Selectors.GetOrAdd(name, static n =>
        {
            Span<byte> buffer = stackalloc byte[256];
            var length = Encoding.ASCII.GetBytes(n, buffer);
            buffer[length] = 0;
            fixed (byte* p = buffer)
            {
                return sel_registerName(p);
            }
        });

    /// <summary>Allocates a new subclass of <paramref name="superclass"/> (not yet registered).</summary>
    public static IntPtr AllocateClassPair(IntPtr superclass, string name)
    {
        Span<byte> buffer = stackalloc byte[256];
        var length = Encoding.ASCII.GetBytes(name, buffer);
        buffer[length] = 0;
        fixed (byte* p = buffer)
        {
            return objc_allocateClassPair(superclass, p, 0);
        }
    }

    /// <summary>Registers a class created with <see cref="AllocateClassPair"/> so it can be instantiated.</summary>
    public static void RegisterClassPair(IntPtr cls) => objc_registerClassPair(cls);

    /// <summary>Adds a method backed by a native function pointer, using an Objective-C type encoding.</summary>
    public static bool AddMethod(IntPtr cls, IntPtr selector, IntPtr implementation, string typeEncoding)
    {
        Span<byte> buffer = stackalloc byte[64];
        var length = Encoding.ASCII.GetBytes(typeEncoding, buffer);
        buffer[length] = 0;
        fixed (byte* p = buffer)
        {
            return class_addMethod(cls, selector, implementation, p) != 0;
        }
    }

    /// <summary>
    /// Creates an immutable <c>NSString</c> (toll-free bridged <c>CFString</c>) from managed text. The
    /// caller owns the result and must release it with <see cref="CFRelease"/>.
    /// </summary>
    public static IntPtr CreateString(string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        var buffer = new byte[byteCount + 1];
        Encoding.UTF8.GetBytes(value, buffer);
        fixed (byte* p = buffer)
        {
            return CFStringCreateWithCString(IntPtr.Zero, p, CFStringEncodingUTF8);
        }
    }

    /// <summary>Reads an <c>NSString</c> as managed text; null for a nil string.</summary>
    public static string? ReadString(IntPtr value) =>
        value == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Send(value, Sel("UTF8String")));

    /// <summary>Starts an autorelease pool for Objective-C calls made on a thread without one.</summary>
    public static IntPtr PushAutoreleasePool() => objc_autoreleasePoolPush();

    public static void PopAutoreleasePool(IntPtr pool) => objc_autoreleasePoolPop(pool);

    /// <summary>Loads a system library / framework by absolute path (used to force class registration).</summary>
    public static IntPtr LoadLibrary(string path) => NativeLibrary.Load(path);

    /// <summary>
    /// Reads a framework-exported <c>NSString * const</c> constant: the exported symbol holds a
    /// pointer to the string object, so the object is one dereference past the symbol address.
    /// </summary>
    public static IntPtr ReadStringConstant(IntPtr library, string symbol) =>
        *(IntPtr*)NativeLibrary.GetExport(library, symbol);

    /// <summary>
    /// Wraps a native function pointer in a global Objective-C block usable as a block argument. The
    /// block captures nothing, so a single instance is valid for the process lifetime and needs no
    /// copy/dispose helpers.
    /// </summary>
    public static IntPtr CreateGlobalBlock(IntPtr invoke)
    {
        EnsureBlockRuntime();

        var block = (BlockLiteral*)NativeMemory.Alloc((nuint)sizeof(BlockLiteral));
        block->Isa = concreteGlobalBlock;
        block->Flags = BlockIsGlobal;
        block->Reserved = 0;
        block->Invoke = invoke;
        block->Descriptor = blockDescriptor;
        return (IntPtr)block;
    }

    /// <summary>
    /// Wraps a native function pointer in a block that carries <paramref name="context"/>, readable in the
    /// callback with <see cref="GetBlockContext"/>. The block is laid out as a stack block, so a callee that
    /// keeps it makes its own copy; the caller frees it with <see cref="FreeContextBlock"/> once the call
    /// that received it returns.
    /// </summary>
    public static IntPtr CreateContextBlock(IntPtr invoke, IntPtr context)
    {
        EnsureBlockRuntime();

        var block = (ContextBlockLiteral*)NativeMemory.Alloc((nuint)sizeof(ContextBlockLiteral));
        block->Isa = concreteStackBlock;
        block->Flags = 0;
        block->Reserved = 0;
        block->Invoke = invoke;
        block->Descriptor = contextBlockDescriptor;
        block->Context = context;
        return (IntPtr)block;
    }

    public static IntPtr GetBlockContext(IntPtr block) => ((ContextBlockLiteral*)block)->Context;

    public static void FreeContextBlock(IntPtr block) => NativeMemory.Free((void*)block);

    private static void EnsureBlockRuntime()
    {
        lock (BlockGate)
        {
            if (concreteGlobalBlock != IntPtr.Zero)
            {
                return;
            }

            // The isa pointers of global and stack blocks are libSystem exports.
            var system = NativeLibrary.Load(LibSystem);
            concreteStackBlock = NativeLibrary.GetExport(system, "_NSConcreteStackBlock");

            var descriptor = (BlockDescriptor*)NativeMemory.Alloc((nuint)sizeof(BlockDescriptor));
            descriptor->Reserved = 0;
            descriptor->Size = (nuint)sizeof(BlockLiteral);
            blockDescriptor = (IntPtr)descriptor;

            var contextDescriptor = (BlockDescriptor*)NativeMemory.Alloc((nuint)sizeof(BlockDescriptor));
            contextDescriptor->Reserved = 0;
            contextDescriptor->Size = (nuint)sizeof(ContextBlockLiteral);
            contextBlockDescriptor = (IntPtr)contextDescriptor;
            concreteGlobalBlock = NativeLibrary.GetExport(system, "_NSConcreteGlobalBlock");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockLiteral
    {
        public IntPtr Isa;
        public int Flags;
        public int Reserved;
        public IntPtr Invoke;
        public IntPtr Descriptor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ContextBlockLiteral
    {
        public IntPtr Isa;
        public int Flags;
        public int Reserved;
        public IntPtr Invoke;
        public IntPtr Descriptor;
        public IntPtr Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockDescriptor
    {
        public nuint Reserved;
        public nuint Size;
    }
}