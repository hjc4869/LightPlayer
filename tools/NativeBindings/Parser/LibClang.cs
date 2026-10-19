using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NativeBindings.Parser;

internal enum CursorKind
{
    StructDecl = 2,
    UnionDecl = 3,
    EnumDecl = 5,
    FieldDecl = 6,
    EnumConstantDecl = 7,
    FunctionDecl = 8,
    VarDecl = 9,
    TypedefDecl = 20,
    MacroDefinition = 501,
    InclusionDirective = 503,
}

internal enum TypeKind
{
    Invalid = 0,
    Unexposed = 1,
    Void = 2,
    Bool = 3,
    CharU = 4,
    UChar = 5,
    Char16 = 6,
    Char32 = 7,
    UShort = 8,
    UInt = 9,
    ULong = 10,
    ULongLong = 11,
    UInt128 = 12,
    CharS = 13,
    SChar = 14,
    WChar = 15,
    Short = 16,
    Int = 17,
    Long = 18,
    LongLong = 19,
    Int128 = 20,
    Float = 21,
    Double = 22,
    LongDouble = 23,
    Pointer = 101,
    Record = 105,
    Enum = 106,
    Typedef = 107,
    FunctionNoProto = 110,
    FunctionProto = 111,
    ConstantArray = 112,
    IncompleteArray = 114,
    Auto = 118,
    Elaborated = 119,
    Attributed = 163,
    Atomic = 177,
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct ClangCursor
{
    public readonly CursorKind Kind;
    private readonly int xdata;
    private readonly nint data0, data1, data2;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct ClangType
{
    public readonly TypeKind Kind;
    private readonly nint data0, data1;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct ClangString
{
    private readonly nint data;
    private readonly uint flags;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct ClangLocation
{
    private readonly nint data0, data1;
    private readonly uint data;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct ClangRange
{
    private readonly nint data0, data1;
    private readonly uint begin, end;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct ClangToken
{
    private readonly uint data0, data1, data2, data3;
    private readonly nint pointer;
}

/// <summary>libclang C API used by the parser. The loaded library is selected by <see cref="Load"/>.</summary>
internal static unsafe class LibClang
{
    private const string Module = "NativeBindings.libclang";
    private static nint handle;

    public const int EvalInt = 1, EvalFloat = 2, EvalStrLiteral = 4;
    public const uint ParseDetailedPreprocessingRecord = 0x01, ParseSkipFunctionBodies = 0x40;

    public static string LoadedPath { get; private set; } = "";

    /// <summary>The builtin header directory of the loaded libclang, or empty when only its own default applies.</summary>
    public static string ResourceDirectory { get; private set; } = "";

    public static void Load(IEnumerable<string> candidates)
    {
        if (handle != 0)
            return;
        var attempts = new List<string>();
        foreach (var candidate in candidates.Distinct(StringComparer.Ordinal))
        {
            if (NativeLibrary.TryLoad(candidate, out var library))
            {
                handle = library;
                LoadedPath = candidate;
                NativeLibrary.SetDllImportResolver(typeof(LibClang).Assembly,
                    (name, _, _) => name == Module ? handle : 0);
                ResourceDirectory = FindResourceDirectory(candidate);
                return;
            }
            attempts.Add(candidate);
        }
        throw new InvalidOperationException("Could not load libclang for the build host architecture " +
            $"{RuntimeInformation.ProcessArchitecture}. Install LLVM/Clang development libraries or set NativeBindingsLibClang. Tried: " +
            string.Join(", ", attempts));
    }

    // Some distributions of libclang (such as the Android NDK's) cannot locate their own builtin headers.
    private static string FindResourceDirectory(string library)
    {
        if (!Path.IsPathFullyQualified(library))
            return "";
        var resolved = new FileInfo(library).ResolveLinkTarget(true)?.FullName ?? library;
        var directory = Path.GetDirectoryName(resolved)!;
        var version = System.Text.RegularExpressions.Regex.Match(Text(clang_getClangVersion()), @"clang version (\d+)").Groups[1].Value;
        var parents = new[] { directory, Path.GetDirectoryName(directory) ?? directory };
        var candidates = parents.SelectMany(parent => new[] { Path.Combine(parent, "clang"), Path.Combine(parent, "lib", "clang"), Path.Combine(parent, "lib64", "clang") })
            .Where(Directory.Exists).ToArray();
        return candidates.Select(root => Path.Combine(root, version)).FirstOrDefault(IsResourceDirectory) ??
            candidates.SelectMany(Directory.EnumerateDirectories).Where(IsResourceDirectory)
                .OrderByDescending(path => int.TryParse(Path.GetFileName(path).Split('.')[0], out var major) ? major : -1).FirstOrDefault() ?? "";
    }

    private static bool IsResourceDirectory(string path) => File.Exists(Path.Combine(path, "include", "stddef.h"));

    public static string Text(ClangString value)
    {
        try
        {
            return Marshal.PtrToStringUTF8(clang_getCString(value)) ?? "";
        }
        finally
        {
            clang_disposeString(value);
        }
    }

    public static string Spelling(ClangCursor cursor) => Text(clang_getCursorSpelling(cursor));

    public static string Spelling(ClangType type) => Text(clang_getTypeSpelling(type));

    public static string FileName(nint file) => file == 0 ? "" : Text(clang_getFileName(file));

    public static List<ClangCursor> Children(ClangCursor cursor)
    {
        var children = new List<ClangCursor>();
        var state = GCHandle.Alloc(children);
        try
        {
            clang_visitChildren(cursor, &VisitChild, GCHandle.ToIntPtr(state));
        }
        finally
        {
            state.Free();
        }
        return children;
    }

    public static List<ClangCursor> Fields(ClangType record)
    {
        var fields = new List<ClangCursor>();
        var state = GCHandle.Alloc(fields);
        try
        {
            clang_Type_visitFields(record, &VisitField, GCHandle.ToIntPtr(state));
        }
        finally
        {
            state.Free();
        }
        return fields;
    }

    public static List<string> Inclusions(nint unit)
    {
        var files = new List<string>();
        var state = GCHandle.Alloc(files);
        try
        {
            clang_getInclusions(unit, &VisitInclusion, GCHandle.ToIntPtr(state));
        }
        finally
        {
            state.Free();
        }
        return files;
    }

    public static int TokenCount(nint unit, ClangCursor cursor)
    {
        clang_tokenize(unit, clang_getCursorExtent(cursor), out var tokens, out var count);
        if (tokens != null)
            clang_disposeTokens(unit, tokens, count);
        return (int)count;
    }

    public static string[] Tokens(nint unit, ClangCursor cursor)
    {
        clang_tokenize(unit, clang_getCursorExtent(cursor), out var tokens, out var count);
        try
        {
            var result = new string[count];
            for (var index = 0; index < count; index++)
                result[index] = Text(clang_getTokenSpelling(unit, tokens[index]));
            return result;
        }
        finally
        {
            if (tokens != null)
                clang_disposeTokens(unit, tokens, count);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int VisitChild(ClangCursor cursor, ClangCursor parent, nint data)
    {
        ((List<ClangCursor>)GCHandle.FromIntPtr(data).Target!).Add(cursor);
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int VisitField(ClangCursor cursor, nint data)
    {
        ((List<ClangCursor>)GCHandle.FromIntPtr(data).Target!).Add(cursor);
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void VisitInclusion(nint file, ClangLocation* stack, uint depth, nint data)
    {
        ((List<string>)GCHandle.FromIntPtr(data).Target!).Add(FileName(file));
    }

    [DllImport(Module)] internal static extern ClangString clang_getClangVersion();
    [DllImport(Module)] internal static extern nint clang_createIndex(int excludeDeclarationsFromPch, int displayDiagnostics);
    [DllImport(Module)] internal static extern void clang_disposeIndex(nint index);
    [DllImport(Module)] internal static extern int clang_parseTranslationUnit2(nint index, byte* sourceFile, byte** arguments,
        int argumentCount, nint unsavedFiles, uint unsavedCount, uint options, out nint unit);
    [DllImport(Module)] internal static extern void clang_disposeTranslationUnit(nint unit);
    [DllImport(Module)] internal static extern ClangCursor clang_getTranslationUnitCursor(nint unit);
    [DllImport(Module)] internal static extern nint clang_getTranslationUnitTargetInfo(nint unit);
    [DllImport(Module)] internal static extern ClangString clang_TargetInfo_getTriple(nint info);
    [DllImport(Module)] internal static extern int clang_TargetInfo_getPointerWidth(nint info);
    [DllImport(Module)] internal static extern void clang_TargetInfo_dispose(nint info);
    [DllImport(Module)] internal static extern uint clang_getNumDiagnostics(nint unit);
    [DllImport(Module)] internal static extern nint clang_getDiagnostic(nint unit, uint index);
    [DllImport(Module)] internal static extern int clang_getDiagnosticSeverity(nint diagnostic);
    [DllImport(Module)] internal static extern ClangString clang_formatDiagnostic(nint diagnostic, uint options);
    [DllImport(Module)] internal static extern void clang_disposeDiagnostic(nint diagnostic);
    [DllImport(Module)] internal static extern ClangLocation clang_getDiagnosticLocation(nint diagnostic);
    [DllImport(Module)] internal static extern nint clang_getIncludedFile(ClangCursor cursor);
    [DllImport(Module)] internal static extern nint clang_getFile(nint unit, byte* fileName);
    [DllImport(Module)] internal static extern ClangLocation clang_getLocation(nint unit, nint file, uint line, uint column);
    [DllImport(Module)] internal static extern nint clang_getCString(ClangString value);
    [DllImport(Module)] internal static extern void clang_disposeString(ClangString value);
    [DllImport(Module)] internal static extern uint clang_visitChildren(ClangCursor cursor,
        delegate* unmanaged[Cdecl]<ClangCursor, ClangCursor, nint, int> visitor, nint data);
    [DllImport(Module)] internal static extern uint clang_Type_visitFields(ClangType type,
        delegate* unmanaged[Cdecl]<ClangCursor, nint, int> visitor, nint data);
    [DllImport(Module)] internal static extern void clang_getInclusions(nint unit,
        delegate* unmanaged[Cdecl]<nint, ClangLocation*, uint, nint, void> visitor, nint data);
    [DllImport(Module)] internal static extern ClangString clang_getCursorSpelling(ClangCursor cursor);
    [DllImport(Module)] internal static extern ClangString clang_getCursorUSR(ClangCursor cursor);
    [DllImport(Module)] internal static extern ClangLocation clang_getCursorLocation(ClangCursor cursor);
    [DllImport(Module)] internal static extern ClangRange clang_getCursorExtent(ClangCursor cursor);
    [DllImport(Module)] internal static extern void clang_getExpansionLocation(ClangLocation location, out nint file,
        out uint line, out uint column, out uint offset);
    [DllImport(Module)] internal static extern void clang_getFileLocation(ClangLocation location, out nint file,
        out uint line, out uint column, out uint offset);
    [DllImport(Module)] internal static extern ClangString clang_getDiagnosticSpelling(nint diagnostic);
    [DllImport(Module)] internal static extern int clang_Location_isInSystemHeader(ClangLocation location);
    [DllImport(Module)] internal static extern ClangString clang_getFileName(nint file);
    [DllImport(Module)] internal static extern ClangType clang_getCursorType(ClangCursor cursor);
    [DllImport(Module)] internal static extern ClangCursor clang_getCursorSemanticParent(ClangCursor cursor);
    [DllImport(Module)] internal static extern ClangType clang_getCanonicalType(ClangType type);
    [DllImport(Module)] internal static extern ClangString clang_getTypeSpelling(ClangType type);
    [DllImport(Module)] internal static extern ClangString clang_getTypedefName(ClangType type);
    [DllImport(Module)] internal static extern ClangType clang_getPointeeType(ClangType type);
    [DllImport(Module)] internal static extern uint clang_isConstQualifiedType(ClangType type);
    [DllImport(Module)] internal static extern ClangType clang_getResultType(ClangType type);
    [DllImport(Module)] internal static extern int clang_getFunctionTypeCallingConv(ClangType type);
    [DllImport(Module)] internal static extern uint clang_isFunctionTypeVariadic(ClangType type);
    [DllImport(Module)] internal static extern int clang_getNumArgTypes(ClangType type);
    [DllImport(Module)] internal static extern ClangType clang_getArgType(ClangType type, uint index);
    [DllImport(Module)] internal static extern uint clang_Cursor_isFunctionInlined(ClangCursor cursor);
    [DllImport(Module)] internal static extern int clang_Cursor_getStorageClass(ClangCursor cursor);
    [DllImport(Module)] internal static extern int clang_getCursorLinkage(ClangCursor cursor);
    [DllImport(Module)] internal static extern int clang_Cursor_getNumArguments(ClangCursor cursor);
    [DllImport(Module)] internal static extern ClangCursor clang_Cursor_getArgument(ClangCursor cursor, uint index);
    [DllImport(Module)] internal static extern ClangCursor clang_getTypeDeclaration(ClangType type);
    [DllImport(Module)] internal static extern ClangType clang_getTypedefDeclUnderlyingType(ClangCursor cursor);
    [DllImport(Module)] internal static extern ClangType clang_Type_getNamedType(ClangType type);
    [DllImport(Module)] internal static extern ClangType clang_Type_getModifiedType(ClangType type);
    [DllImport(Module)] internal static extern ClangType clang_getEnumDeclIntegerType(ClangCursor cursor);
    [DllImport(Module)] internal static extern long clang_getEnumConstantDeclValue(ClangCursor cursor);
    [DllImport(Module)] internal static extern ulong clang_getEnumConstantDeclUnsignedValue(ClangCursor cursor);
    [DllImport(Module)] internal static extern ClangCursor clang_getCursorDefinition(ClangCursor cursor);
    [DllImport(Module)] internal static extern int clang_Cursor_isNull(ClangCursor cursor);
    [DllImport(Module)] internal static extern uint clang_isCursorDefinition(ClangCursor cursor);
    [DllImport(Module)] internal static extern uint clang_Cursor_isAnonymousRecordDecl(ClangCursor cursor);
    [DllImport(Module)] internal static extern uint clang_Cursor_isBitField(ClangCursor cursor);
    [DllImport(Module)] internal static extern int clang_getFieldDeclBitWidth(ClangCursor cursor);
    [DllImport(Module)] internal static extern long clang_Cursor_getOffsetOfField(ClangCursor cursor);
    [DllImport(Module)] internal static extern long clang_Type_getSizeOf(ClangType type);
    [DllImport(Module)] internal static extern long clang_Type_getAlignOf(ClangType type);
    [DllImport(Module)] internal static extern ClangType clang_getArrayElementType(ClangType type);
    [DllImport(Module)] internal static extern long clang_getArraySize(ClangType type);
    [DllImport(Module)] internal static extern uint clang_Cursor_isMacroFunctionLike(ClangCursor cursor);
    [DllImport(Module)] internal static extern uint clang_Cursor_isMacroBuiltin(ClangCursor cursor);
    [DllImport(Module)] internal static extern void clang_tokenize(nint unit, ClangRange range, out ClangToken* tokens, out uint count);
    [DllImport(Module)] internal static extern ClangString clang_getTokenSpelling(nint unit, ClangToken token);
    [DllImport(Module)] internal static extern void clang_disposeTokens(nint unit, ClangToken* tokens, uint count);
    [DllImport(Module)] internal static extern nint clang_Cursor_Evaluate(ClangCursor cursor);
    [DllImport(Module)] internal static extern int clang_EvalResult_getKind(nint result);
    [DllImport(Module)] internal static extern uint clang_EvalResult_isUnsignedInt(nint result);
    [DllImport(Module)] internal static extern ulong clang_EvalResult_getAsUnsigned(nint result);
    [DllImport(Module)] internal static extern long clang_EvalResult_getAsLongLong(nint result);
    [DllImport(Module)] internal static extern double clang_EvalResult_getAsDouble(nint result);
    [DllImport(Module)] internal static extern nint clang_EvalResult_getAsStr(nint result);
    [DllImport(Module)] internal static extern void clang_EvalResult_dispose(nint result);
}
