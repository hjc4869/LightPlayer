using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis.CSharp;

namespace NativeBindings.Parser;

/// <summary>Extracts functions, constants, and types from the requested headers through libclang.</summary>
internal sealed unsafe class HeaderParser : IDisposable
{
    private const string MacroPrefix = "__nb_macro_";
    private readonly NativeTarget target;
    private readonly ExportCatalog exports;
    private readonly string workDirectory;
    private readonly nint index = LibClang.clang_createIndex(0, 0);
    private readonly Ownership ownership = new();
    private readonly List<MacroDefinition> macros = [];
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly Dictionary<string, HashSet<string>> includeGraph = new(PathComparer);
    private readonly Dictionary<string, string> resolvedHeaders = new(StringComparer.Ordinal);
    private TypeModel types = null!;

    public HeaderParser(NativeTarget target, ExportCatalog exports, string workDirectory)
    {
        this.target = target;
        this.exports = exports;
        this.workDirectory = workDirectory;
        if (index == 0)
            throw new InvalidOperationException("libclang could not create an index.");
    }

    public string Triple { get; private set; } = "";
    public int PointerBits { get; private set; }
    public int LongBits { get; private set; }

    public XElement Parse(IReadOnlyList<HeaderRequest> requests)
    {
        Probe(requests.Select(request => request.Header).Distinct(StringComparer.Ordinal).ToArray());
        types = new TypeModel(target, LongBits, cursor => ownership.Owns(DeclarationFile(cursor)));
        var document = new XElement("bindings");
        foreach (var request in requests)
            document.Add(ParseRequest(request));
        document.Add(new XElement("types", types.Declarations));
        return document;
    }

    public void Dispose() => LibClang.clang_disposeIndex(index);

    private void Probe(string[] headers)
    {
        var source = new StringBuilder();
        foreach (var header in headers)
            source.Append("#include <").Append(header).Append(">\n");
        source.Append("static __auto_type __nb_probe_pointer = sizeof(void *);\nstatic __auto_type __nb_probe_long = sizeof(long);\n");
        var mainFile = Path.Combine(workDirectory, "probe.c");
        var unit = ParseUnit(mainFile, source.ToString(), LibClang.ParseDetailedPreprocessingRecord | LibClang.ParseSkipFunctionBodies,
            out var diagnostics);
        try
        {
            ThrowOnErrors(diagnostics, "the requested headers", _ => true);
            var info = LibClang.clang_getTranslationUnitTargetInfo(unit);
            try
            {
                Triple = LibClang.Text(LibClang.clang_TargetInfo_getTriple(info));
                PointerBits = LibClang.clang_TargetInfo_getPointerWidth(info);
            }
            finally
            {
                LibClang.clang_TargetInfo_dispose(info);
            }

            var mainIncludes = new List<(string File, bool System)>();
            var quotedIncludes = new List<(string Includer, string Included)>();
            foreach (var cursor in LibClang.Children(LibClang.clang_getTranslationUnitCursor(unit)))
            {
                switch (cursor.Kind)
                {
                    case CursorKind.InclusionDirective:
                    {
                        var includer = DeclarationFile(cursor);
                        var file = LibClang.clang_getIncludedFile(cursor);
                        var included = FileKey(LibClang.FileName(file));
                        if (included.Length == 0)
                            continue;
                        if (!includeGraph.TryGetValue(includer, out var edges))
                            includeGraph[includer] = edges = new HashSet<string>(PathComparer);
                        edges.Add(included);
                        if (IsSameFile(includer, mainFile))
                            mainIncludes.Add((included, LibClang.clang_Location_isInSystemHeader(LibClang.clang_getLocation(unit, file, 1, 1)) != 0));
                        else if (LibClang.Tokens(unit, cursor) is [_, _, var name, ..] && name.StartsWith('"'))
                            quotedIncludes.Add((includer, included));
                        break;
                    }
                    case CursorKind.MacroDefinition when LibClang.clang_Cursor_isMacroBuiltin(cursor) == 0:
                    {
                        var tokens = LibClang.Tokens(unit, cursor);
                        macros.Add(new MacroDefinition(LibClang.Spelling(cursor), DeclarationFile(cursor),
                            LibClang.clang_Cursor_isMacroFunctionLike(cursor) != 0, tokens.Length > 1, tokens is [_, var only] && only == tokens[0]));
                        break;
                    }
                    case CursorKind.VarDecl when LibClang.Spelling(cursor) is "__nb_probe_pointer" or "__nb_probe_long":
                    {
                        var size = (int)EvaluateInteger(cursor);
                        if (LibClang.Spelling(cursor) == "__nb_probe_long")
                            LongBits = size * 8;
                        else if (size * 8 != PointerBits)
                            throw new InvalidOperationException($"libclang reports inconsistent pointer widths for {Triple}.");
                        break;
                    }
                }
            }
            if (mainIncludes.Count != headers.Length)
                throw new InvalidOperationException("libclang did not resolve every requested header.");
            if (target.ExpectedPointerBits != 0 && (PointerBits != target.ExpectedPointerBits || LongBits != target.ExpectedLongBits))
                throw new InvalidOperationException($"The compiler ABI of {Triple} (pointer {PointerBits}, long {LongBits} bits) disagrees with the native " +
                    $"profile (pointer {target.ExpectedPointerBits}, long {target.ExpectedLongBits} bits).");
            if (PointerBits != (target.IsBrowser || target.Architecture is "x86" or "arm" ? 32 : 64))
                throw new InvalidOperationException($"The compiler target {Triple} does not match runtime {target.Runtime}.");
            for (var header = 0; header < headers.Length; header++)
            {
                resolvedHeaders[headers[header]] = mainIncludes[header].File;
                ownership.AddRequest(headers[header], mainIncludes[header].File, mainIncludes[header].System, target.LibraryIncludeRoots);
            }
            ownership.AddQuotedIncludes(quotedIncludes);
            foreach (var file in LibClang.Inclusions(unit))
                Dependency(file);
        }
        finally
        {
            LibClang.clang_disposeTranslationUnit(unit);
        }
    }

    private XElement ParseRequest(HeaderRequest request)
    {
        var node = new XElement("header", new XAttribute("name", request.Header));
        if (request.Macros is not null)
            node.Add(new XAttribute("macros", string.Join(' ', request.Macros)));

        string[] candidates;
        if (request.Macros is not null)
            candidates = request.Macros.ToArray();
        else
        {
            var reachable = Reachable(resolvedHeaders[request.Header]);
            var owned = macros.Where(macro => reachable.Contains(macro.File) && ownership.Owns(macro.File)).ToArray();
            foreach (var name in owned.Where(macro => macro.FunctionLike).Select(macro => macro.Name).Distinct(StringComparer.Ordinal))
                Exclude(node, name, "macro", "function-like macro");
            candidates = owned.Where(macro => !macro.FunctionLike && macro.HasBody && !macro.SelfReference).Select(macro => macro.Name)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        }

        var source = new StringBuilder("#include <").Append(request.Header).Append(">\n");
        foreach (var macro in candidates)
            source.Append("#ifdef ").Append(macro).Append('\n').Append("static __auto_type ").Append(MacroPrefix).Append(macro)
                .Append(" = ").Append(macro).Append(";\n#endif\n");
        var mainFile = Path.Combine(workDirectory, string.Concat(request.Key.Select(character => char.IsAsciiLetterOrDigit(character) ? character : '_')) + ".c");
        var unit = ParseUnit(mainFile, source.ToString(), LibClang.ParseSkipFunctionBodies, out var diagnostics);
        try
        {
            ThrowOnErrors(diagnostics, request.Header, diagnostic => !IsSameFile(diagnostic.File, mainFile) || diagnostic.Line < 3);
            var macroErrors = diagnostics.Where(diagnostic => diagnostic.Severity >= 3 && IsSameFile(diagnostic.File, mainFile) && diagnostic.Line >= 3)
                .GroupBy(diagnostic => (int)(diagnostic.Line - 3) / 3)
                .ToDictionary(group => group.Key, group => group.First().Summary);
            var children = LibClang.Children(LibClang.clang_getTranslationUnitCursor(unit));
            var functions = new HashSet<string>(StringComparer.Ordinal);
            var constants = new HashSet<string>(StringComparer.Ordinal);
            if (request.Macros is null)
            {
                foreach (var cursor in children.Where(cursor => cursor.Kind == CursorKind.TypedefDecl && Owns(cursor)))
                    types.RegisterTypedef(cursor);
                foreach (var cursor in children.Where(Owns))
                {
                    switch (cursor.Kind)
                    {
                        case CursorKind.FunctionDecl when functions.Add(LibClang.Spelling(cursor)):
                            AddFunction(node, cursor);
                            break;
                        case CursorKind.EnumDecl when LibClang.clang_isCursorDefinition(cursor) != 0:
                            AddEnum(node, cursor, constants);
                            break;
                        case CursorKind.StructDecl or CursorKind.UnionDecl when LibClang.clang_isCursorDefinition(cursor) != 0 &&
                            LibClang.clang_Cursor_isAnonymousRecordDecl(cursor) == 0 && IsNamed(cursor):
                            try
                            {
                                types.Record(cursor);
                            }
                            catch (NotSupportedException exception)
                            {
                                Exclude(node, LibClang.Spelling(cursor), "record", exception.Message);
                            }
                            break;
                        case CursorKind.VarDecl when LibClang.clang_getCursorLinkage(cursor) == 4:
                            Exclude(node, LibClang.Spelling(cursor), "variable", "exported data has no function import");
                            break;
                    }
                }
            }

            var evaluated = new HashSet<string>(StringComparer.Ordinal);
            foreach (var cursor in children.Where(cursor => cursor.Kind == CursorKind.VarDecl))
            {
                var name = LibClang.Spelling(cursor);
                if (!name.StartsWith(MacroPrefix, StringComparison.Ordinal))
                    continue;
                var macro = name[MacroPrefix.Length..];
                evaluated.Add(macro);
                if (functions.Contains(macro) || !constants.Add(macro))
                    continue;
                var position = System.Array.IndexOf(candidates, macro);
                if (macroErrors.TryGetValue(position, out var error))
                    Exclude(node, macro, "macro", "not a constant expression: " + error);
                else if (EvaluateMacro(cursor) is ({ } type, { } value))
                    node.Add(new XElement("constant", new XAttribute("name", macro), new XAttribute("type", type),
                        new XAttribute("value", value), new XAttribute("origin", "macro")));
                else
                    Exclude(node, macro, "macro", "not an integer, floating-point, or string constant");
            }
            foreach (var macro in candidates.Where(macro => !evaluated.Contains(macro)))
            {
                if (request.Macros is not null)
                    Exclude(node, macro, "macro", "macro is not defined by " + request.Header);
                else if (macroErrors.TryGetValue(System.Array.IndexOf(candidates, macro), out var error))
                    Exclude(node, macro, "macro", "not a constant expression: " + error);
            }
            foreach (var file in LibClang.Inclusions(unit))
                Dependency(file);
        }
        finally
        {
            LibClang.clang_disposeTranslationUnit(unit);
        }
        return node;
    }

    private void AddFunction(XElement node, ClangCursor cursor)
    {
        var name = LibClang.Spelling(cursor);
        var type = LibClang.clang_getCursorType(cursor);
        var module = exports.Resolve(name);
        if (LibClang.clang_getCursorLinkage(cursor) != 4 || (module is null && LibClang.clang_Cursor_isFunctionInlined(cursor) != 0))
        {
            Exclude(node, name, "function", "header-only function has no exported symbol");
            return;
        }
        if (type.Kind != TypeKind.FunctionProto)
        {
            Exclude(node, name, "function", "function has no prototype");
            return;
        }
        if (LibClang.clang_isFunctionTypeVariadic(type) != 0)
        {
            Exclude(node, name, "function", "variadic function");
            return;
        }
        if (module is null)
        {
            Exclude(node, name, "function", "no selected native library exports it");
            return;
        }
        try
        {
            var convention = types.CallingConvention(type) ??
                throw new NotSupportedException($"calling convention {LibClang.clang_getFunctionTypeCallingConv(type)}");
            var result = LibClang.clang_getResultType(type);
            var function = new XElement("function", new XAttribute("name", name), new XAttribute("module", module),
                new XAttribute("callingConvention", convention), new XAttribute("return", types.Lower(result, TypeUse.Return)),
                new XAttribute("stringReturn", IsConstCharPointer(result)));
            var parameterNames = new HashSet<string>(StringComparer.Ordinal);
            var count = LibClang.clang_Cursor_getNumArguments(cursor);
            for (uint index = 0; index < count; index++)
            {
                var parameter = LibClang.clang_Cursor_getArgument(cursor, index);
                var parameterType = LibClang.clang_getCursorType(parameter);
                var parameterName = LibClang.Spelling(parameter);
                if (parameterName.Length == 0 || !parameterNames.Add(parameterName))
                    parameterNames.Add(parameterName = "argument" + index.ToString(CultureInfo.InvariantCulture));
                function.Add(new XElement("parameter", new XAttribute("name", parameterName),
                    new XAttribute("type", types.LowerParameter(parameterType)), new XAttribute("string", IsConstCharPointer(parameterType))));
            }
            node.Add(function);
        }
        catch (NotSupportedException exception)
        {
            Exclude(node, name, "function", exception.Message);
        }
    }

    private void AddEnum(XElement node, ClangCursor cursor, HashSet<string> constants)
    {
        string underlying;
        try
        {
            if (IsNamed(cursor))
                types.Enum(cursor);
            underlying = types.Integer(LibClang.clang_getCanonicalType(LibClang.clang_getEnumDeclIntegerType(cursor)));
        }
        catch (NotSupportedException exception)
        {
            Exclude(node, LibClang.Spelling(cursor), "enum", exception.Message);
            return;
        }
        foreach (var item in LibClang.Children(cursor).Where(item => item.Kind == CursorKind.EnumConstantDecl))
        {
            var name = LibClang.Spelling(item);
            if (constants.Add(name))
                node.Add(new XElement("constant", new XAttribute("name", name), new XAttribute("type", underlying),
                    new XAttribute("value", TypeModel.IntegerLiteral(underlying, TypeModel.EnumValue(item, underlying))), new XAttribute("origin", "enum")));
        }
    }

    private (string? Type, string? Value) EvaluateMacro(ClangCursor variable)
    {
        var type = LibClang.clang_getCanonicalType(LibClang.clang_getCursorType(variable));
        if (type.Kind is TypeKind.Int128 or TypeKind.UInt128 || LibClang.clang_Type_getSizeOf(type) > 8)
            return default;
        var result = LibClang.clang_Cursor_Evaluate(variable);
        if (result == 0)
            return default;
        try
        {
            switch (LibClang.clang_EvalResult_getKind(result))
            {
                case LibClang.EvalInt:
                {
                    if (type.Kind == TypeKind.Enum)
                        type = LibClang.clang_getCanonicalType(LibClang.clang_getEnumDeclIntegerType(LibClang.clang_getTypeDeclaration(type)));
                    if (type.Kind is TypeKind.Pointer or TypeKind.Record or TypeKind.Float or TypeKind.Double or TypeKind.LongDouble)
                        return default;
                    var csharp = type.Kind == TypeKind.Bool ? "byte" : types.Integer(type);
                    var bits = LibClang.clang_EvalResult_isUnsignedInt(result) != 0
                        ? LibClang.clang_EvalResult_getAsUnsigned(result)
                        : unchecked((ulong)LibClang.clang_EvalResult_getAsLongLong(result));
                    return (csharp, TypeModel.IntegerLiteral(csharp, bits));
                }
                case LibClang.EvalFloat:
                {
                    var value = LibClang.clang_EvalResult_getAsDouble(result);
                    return type.Kind switch
                    {
                        TypeKind.Float => ("float", FloatLiteral((float)value)),
                        TypeKind.Double => ("double", DoubleLiteral(value)),
                        TypeKind.LongDouble when LibClang.clang_Type_getSizeOf(type) == 8 => ("double", DoubleLiteral(value)),
                        _ => default
                    };
                }
                case LibClang.EvalStrLiteral when type.Kind == TypeKind.Pointer &&
                    LibClang.clang_getCanonicalType(LibClang.clang_getPointeeType(type)).Kind is TypeKind.CharS or TypeKind.CharU:
                {
                    var pointer = (byte*)LibClang.clang_EvalResult_getAsStr(result);
                    if (pointer == null)
                        return default;
                    try
                    {
                        var text = new UTF8Encoding(false, true).GetString(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(pointer));
                        return ("string", SymbolDisplay.FormatLiteral(text, true));
                    }
                    catch (DecoderFallbackException)
                    {
                        return default;
                    }
                }
                default:
                    return default;
            }
        }
        catch (NotSupportedException)
        {
            return default;
        }
        finally
        {
            LibClang.clang_EvalResult_dispose(result);
        }
    }

    private static string DoubleLiteral(double value) =>
        double.IsNaN(value) ? "double.NaN" : double.IsPositiveInfinity(value) ? "double.PositiveInfinity" :
        double.IsNegativeInfinity(value) ? "double.NegativeInfinity" : value.ToString("R", CultureInfo.InvariantCulture) + "D";

    private static string FloatLiteral(float value) =>
        float.IsNaN(value) ? "float.NaN" : float.IsPositiveInfinity(value) ? "float.PositiveInfinity" :
        float.IsNegativeInfinity(value) ? "float.NegativeInfinity" : value.ToString("R", CultureInfo.InvariantCulture) + "F";

    private static long EvaluateInteger(ClangCursor variable)
    {
        var result = LibClang.clang_Cursor_Evaluate(variable);
        if (result == 0 || LibClang.clang_EvalResult_getKind(result) != LibClang.EvalInt)
            throw new InvalidOperationException($"libclang could not evaluate {LibClang.Spelling(variable)}.");
        try
        {
            return LibClang.clang_EvalResult_getAsLongLong(result);
        }
        finally
        {
            LibClang.clang_EvalResult_dispose(result);
        }
    }

    private static bool IsConstCharPointer(ClangType type)
    {
        var canonical = LibClang.clang_getCanonicalType(type);
        if (canonical.Kind != TypeKind.Pointer)
            return false;
        var pointee = LibClang.clang_getPointeeType(canonical);
        return LibClang.clang_isConstQualifiedType(pointee) != 0 &&
            LibClang.clang_getCanonicalType(pointee).Kind is TypeKind.CharS or TypeKind.CharU;
    }

    private bool IsNamed(ClangCursor cursor) =>
        SyntaxFacts.IsValidIdentifier(LibClang.Spelling(cursor)) || types.HasName(cursor);

    private bool Owns(ClangCursor cursor) => ownership.Owns(DeclarationFile(cursor));

    private static string DeclarationFile(ClangCursor cursor)
    {
        LibClang.clang_getExpansionLocation(LibClang.clang_getCursorLocation(cursor), out var file, out _, out _, out _);
        return FileKey(LibClang.FileName(file));
    }

    // libclang spells a file as it was looked up, which on Windows can mix directory separators.
    private static string FileKey(string path) => path.Length == 0 ? path : Path.GetFullPath(path);

    private HashSet<string> Reachable(string header)
    {
        var reachable = new HashSet<string>(PathComparer) { header };
        var pending = new Queue<string>([header]);
        while (pending.TryDequeue(out var file))
        {
            if (!includeGraph.TryGetValue(file, out var edges))
                continue;
            foreach (var included in edges)
                if (reachable.Add(included))
                    pending.Enqueue(included);
        }
        return reachable;
    }

    private nint ParseUnit(string mainFile, string contents, uint options, out List<Diagnostic> diagnostics)
    {
        File.WriteAllText(mainFile, contents);
        var arguments = target.CompilerArguments().Concat(LibClang.ResourceDirectory.Length != 0 ? ["-resource-dir", LibClang.ResourceDirectory] : [])
            .Concat(["-ferror-limit=0", "-Wno-everything"]).ToArray();
        var allocations = new List<nint>();
        nint unit;
        try
        {
            var file = Utf8(mainFile, allocations);
            var argv = stackalloc byte*[arguments.Length];
            for (var argument = 0; argument < arguments.Length; argument++)
                argv[argument] = Utf8(arguments[argument], allocations);
            var code = LibClang.clang_parseTranslationUnit2(index, file, argv, arguments.Length, 0, 0, options, out unit);
            if (code != 0 || unit == 0)
                throw new InvalidOperationException($"libclang failed to parse {mainFile} (error {code}). Arguments: {string.Join(' ', arguments)}");
        }
        finally
        {
            foreach (var allocation in allocations)
                Marshal.FreeCoTaskMem(allocation);
        }

        diagnostics = [];
        var log = new StringBuilder();
        for (uint position = 0; position < LibClang.clang_getNumDiagnostics(unit); position++)
        {
            var diagnostic = LibClang.clang_getDiagnostic(unit, position);
            try
            {
                LibClang.clang_getExpansionLocation(LibClang.clang_getDiagnosticLocation(diagnostic), out var file, out var line, out _, out _);
                LibClang.clang_getFileLocation(LibClang.clang_getDiagnosticLocation(diagnostic), out var spellingFile, out var spellingLine, out _, out _);
                var message = LibClang.Text(LibClang.clang_formatDiagnostic(diagnostic, 0x0b));
                var spelling = LibClang.FileName(spellingFile);
                var summary = (spelling.Length != 0 && !IsSameFile(spelling, mainFile) ? $"{Path.GetFileName(spelling)}:{spellingLine}: " : "") +
                    LibClang.Text(LibClang.clang_getDiagnosticSpelling(diagnostic));
                diagnostics.Add(new Diagnostic(LibClang.clang_getDiagnosticSeverity(diagnostic), LibClang.FileName(file), line, message, summary));
                log.AppendLine(message);
            }
            finally
            {
                LibClang.clang_disposeDiagnostic(diagnostic);
            }
        }
        File.WriteAllText(mainFile + ".diagnostics.txt", log.ToString());
        return unit;
    }

    private static byte* Utf8(string value, List<nint> allocations)
    {
        var pointer = Marshal.StringToCoTaskMemUTF8(value);
        allocations.Add(pointer);
        return (byte*)pointer;
    }

    private void ThrowOnErrors(List<Diagnostic> diagnostics, string subject, Func<Diagnostic, bool> relevant)
    {
        var errors = diagnostics.Where(diagnostic => diagnostic.Severity >= 3 && relevant(diagnostic)).Select(diagnostic => diagnostic.Message).ToArray();
        if (errors.Length != 0)
            throw new InvalidOperationException($"libclang ({LibClang.LoadedPath}) could not parse {subject} for {target.Runtime} ({target.Provider}):\n" +
                string.Join('\n', errors.Take(20)) + (errors.Length > 20 ? $"\n... and {errors.Length - 20} more errors" : "") +
                "\nInstall the matching development headers or check the configured sysroot and toolchain.");
    }

    private void Dependency(string file)
    {
        if (file.Length != 0 && File.Exists(file) && !file.StartsWith(workDirectory, StringComparison.Ordinal))
            target.DependencyFiles.Add(Path.GetFullPath(file));
    }

    private static bool IsSameFile(string left, string right) =>
        left.Length != 0 && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void Exclude(XElement node, string name, string kind, string reason) =>
        node.Add(new XElement("excluded", new XAttribute("name", name), new XAttribute("kind", kind), new XAttribute("reason", reason)));

    private sealed record MacroDefinition(string Name, string File, bool FunctionLike, bool HasBody, bool SelfReference);

    private sealed record Diagnostic(int Severity, string File, uint Line, string Message, string Summary);
}
