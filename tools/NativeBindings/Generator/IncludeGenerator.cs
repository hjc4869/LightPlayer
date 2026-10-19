using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace LightStudio.NativeBindings.Generator;

/// <summary>Emits native imports, constants, and types for <c>[Include]</c> declarations from the build's binding manifest.</summary>
[Generator]
public sealed class IncludeGenerator : IIncrementalGenerator
{
    private const string AttributeName = "LightStudio.NativeBindings.IncludeAttribute";
    private const string InteropServices = "global::System.Runtime.InteropServices.";
    private const string CompilerServices = "global::System.Runtime.CompilerServices.";
    private static readonly DiagnosticDescriptor InvalidBinding = new("NB2001", "Invalid native binding",
        "{0}", "NativeBindings", DiagnosticSeverity.Error, true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(output => output.AddSource("IncludeAttribute.g.cs", SourceText.From("""
            #nullable enable
            namespace LightStudio.NativeBindings
            {
                /// <summary>Generates members for the declarations of a C header of the selected native libraries.</summary>
                [global::System.AttributeUsage(global::System.AttributeTargets.Class | global::System.AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
                internal sealed class IncludeAttribute : global::System.Attribute
                {
                    public IncludeAttribute(string header) { Header = header; }
                    /// <summary>Header path relative to the native include roots, using '/' separators.</summary>
                    public string Header { get; }
                    /// <summary>Generated nested class receiving the members; members are added to the annotated type when null.</summary>
                    public string? Class { get; set; }
                    /// <summary><c>BindingFlags.Public</c> or <c>BindingFlags.NonPublic</c> for the generated members.</summary>
                    public global::System.Reflection.BindingFlags Access { get; set; } = global::System.Reflection.BindingFlags.NonPublic;
                    /// <summary>Overrides the P/Invoke module name of dynamically linked imports.</summary>
                    public string? ImportName { get; set; }
                    /// <summary>Selects only these macros, evaluated after including the header, wherever they are defined.</summary>
                    public string[]? Macros { get; set; }
                }
            }
            """, Encoding.UTF8)));

        var targets = context.SyntaxProvider.ForAttributeWithMetadataName(AttributeName,
            static (node, _) => node is TypeDeclarationSyntax,
            static (syntax, _) => Target.Create((INamedTypeSymbol)syntax.TargetSymbol)).Collect();
        var manifest = context.AdditionalTextsProvider
            .Where(static file => file.Path.EndsWith(".nativebindings.xml", StringComparison.Ordinal))
            .Select(static (file, cancellation) => file.GetText(cancellation)?.ToString() ?? "")
            .Collect()
            .Select(static (texts, _) => Manifest.Parse(texts));
        var settings = context.AnalyzerConfigOptionsProvider.Select(static (options, _) =>
        {
            options.GlobalOptions.TryGetValue("build_property.NativeBindingsRuntime", out var runtime);
            options.GlobalOptions.TryGetValue("build_property.NativeBindingsEmitImports", out var emitImports);
            return (Runtime: runtime ?? "", EmitImports: !string.Equals(emitImports, "false", StringComparison.OrdinalIgnoreCase));
        });
        var referencedTypes = manifest.Combine(context.CompilationProvider).Select(static (input, _) => ReferencedTypes(input.Left, input.Right));

        context.RegisterSourceOutput(targets.Combine(manifest).Combine(referencedTypes), static (output, input) =>
        {
            var ((targetList, catalog), referenced) = input;
            if (targetList.Length != 0 && catalog.Error is null)
                EmitTypes(output, catalog, referenced);
        });
        context.RegisterSourceOutput(targets.Combine(manifest).Combine(settings), static (output, input) =>
        {
            var ((targetList, catalog), (runtime, emitImports)) = input;
            if (targetList.Length != 0)
                EmitTargets(output, targetList, catalog, runtime, emitImports);
        });
    }

    private static EquatableArray<string> ReferencedTypes(Manifest manifest, Compilation compilation)
    {
        if (manifest.Root is null)
            return default;
        var names = new List<string>();
        foreach (var type in manifest.Root.Elements("types").Elements())
        {
            var name = (string)type.Attribute("name")!;
            var symbol = compilation.GetTypeByMetadataName(manifest.TypeNamespace + "." + name);
            if (symbol is not null && !SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, compilation.Assembly) &&
                compilation.IsSymbolAccessibleWithin(symbol, compilation.Assembly))
                names.Add(name);
        }
        return new EquatableArray<string>(names.ToImmutableArray());
    }

    private static void EmitTypes(SourceProductionContext output, Manifest manifest, EquatableArray<string> referenced)
    {
        var existing = new HashSet<string>(referenced, StringComparer.Ordinal);
        var source = new StringBuilder("#nullable enable\nnamespace ").Append(manifest.TypeNamespace).AppendLine(" {");
        var bitfields = false;
        foreach (var type in manifest.Root!.Elements("types").Elements())
        {
            if (existing.Contains((string)type.Attribute("name")!))
                continue;
            bitfields |= type.Elements("bitfield").Any();
            source.AppendLine(RenderType(type));
        }
        if (bitfields)
            source.AppendLine($$"""
                internal static class NativeBitfield
                {
                    public static ulong Read(ref byte storage, int bitOffset, int width, bool signed)
                    {
                        ulong value = 0;
                        for (var bit = 0; bit < width; bit++)
                        {
                            var position = bitOffset + bit;
                            if ((({{CompilerServices}}Unsafe.Add(ref storage, position >> 3) >> (position & 7)) & 1) != 0)
                                value |= 1UL << bit;
                        }
                        if (signed && width < 64 && (value & (1UL << (width - 1))) != 0)
                            value |= ulong.MaxValue << width;
                        return value;
                    }

                    public static void Write(ref byte storage, int bitOffset, int width, ulong value)
                    {
                        for (var bit = 0; bit < width; bit++)
                        {
                            var position = bitOffset + bit;
                            ref var target = ref {{CompilerServices}}Unsafe.Add(ref storage, position >> 3);
                            var mask = (byte)(1 << (position & 7));
                            target = ((value >> bit) & 1) != 0 ? (byte)(target | mask) : (byte)(target & ~mask);
                        }
                    }
                }
                """);
        source.AppendLine("}");
        output.AddSource("NativeTypes.g.cs", SourceText.From(source.ToString(), Encoding.UTF8));
    }

    private static void EmitTargets(SourceProductionContext output, ImmutableArray<Target> targets, Manifest manifest, string runtime, bool emitImports)
    {
        if (manifest.Error is not null)
        {
            Error(output, null, manifest.Error);
            return;
        }
        var catalog = manifest.Root!;
        if ((string?)catalog.Attribute("runtime") != runtime)
        {
            Error(output, null, $"The native binding manifest targets '{(string?)catalog.Attribute("runtime")}', not NativeBindingsRuntime '{runtime}'. Rebuild the project.");
            return;
        }
        var linkage = (string?)catalog.Attribute("linkage");
        var headers = catalog.Elements("header").ToDictionary(header => HeaderKey((string)header.Attribute("name")!, (string?)header.Attribute("macros")));

        foreach (var target in targets.GroupBy(target => target.DisplayName).Select(group => group.First()))
        {
            if (target.Error is not null)
            {
                Error(output, target.Location, target.Error);
                continue;
            }
            var source = new StringBuilder("#nullable enable\nusing ").Append(manifest.TypeNamespace).AppendLine(";");
            if (target.Namespace.Length != 0)
                source.Append("namespace ").Append(target.Namespace).AppendLine(" {");
            foreach (var declaration in target.Hierarchy)
                source.Append(declaration).AppendLine(" {");

            foreach (var group in target.Includes.GroupBy(include => include.Class ?? ""))
            {
                if (group.Key.Length != 0 && (!SyntaxFacts.IsValidIdentifier(group.Key) || group.Key.StartsWith("@", StringComparison.Ordinal)))
                {
                    Error(output, target.Location, $"Invalid nested class name: {group.Key}");
                    continue;
                }
                var accessValues = group.Select(include => include.Access).Distinct().ToArray();
                if (accessValues.Length != 1 || accessValues[0] is not (16 or 32))
                {
                    Error(output, target.Location, "Access must consistently be BindingFlags.Public or BindingFlags.NonPublic within each generated class.");
                    continue;
                }
                var access = accessValues[0] == 16 ? "public" : "private";
                if (group.Key.Length != 0)
                    source.Append(access).Append(" static partial class @").Append(group.Key).AppendLine(" {");

                var members = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var include in group)
                {
                    if (!headers.TryGetValue(HeaderKey(include.Header, include.Macros), out var header))
                    {
                        Error(output, target.Location, $"Header {include.Header} is absent from the native binding manifest. Rebuild the project; Include arguments must be literals.");
                        continue;
                    }
                    if (include.ImportName is not null && linkage != "dynamic")
                    {
                        Error(output, target.Location, $"Include.ImportName applies only to dynamically linked imports; this build uses {linkage} linkage.");
                        continue;
                    }
                    if (include.Macros is not null)
                    {
                        foreach (var excluded in header.Elements("excluded"))
                            Error(output, target.Location, $"Macro {(string)excluded.Attribute("name")!} from {include.Header} is unavailable: {(string)excluded.Attribute("reason")!}");
                    }
                    foreach (var member in header.Elements().Where(element => element.Name == "function" || element.Name == "constant"))
                    {
                        var name = (string)member.Attribute("name")!;
                        var declaration = member.Name == "constant"
                            ? $"{access} const {(string)member.Attribute("type")!} @{name} = {(string)member.Attribute("value")!};"
                            : RenderFunction(member, access, include.ImportName, emitImports);
                        if (members.TryGetValue(name, out var previous) && previous != declaration)
                        {
                            Error(output, target.Location, $"Conflicting native declarations for {name} in {target.DisplayName}.{group.Key}");
                            continue;
                        }
                        members[name] = declaration;
                    }
                }
                foreach (var member in members.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    source.AppendLine(member.Value);
                if (group.Key.Length != 0)
                    source.AppendLine("}");
            }

            foreach (var _ in target.Hierarchy)
                source.AppendLine("}");
            if (target.Namespace.Length != 0)
                source.AppendLine("}");
            output.AddSource(HintName(target.DisplayName) + ".g.cs", SourceText.From(source.ToString(), Encoding.UTF8));
        }
    }

    private static string RenderFunction(XElement member, string access, string? importName, bool emitImports)
    {
        var name = (string)member.Attribute("name")!;
        var module = importName ?? (string)member.Attribute("module")!;
        var convention = (string?)member.Attribute("callingConvention") ?? "Cdecl";
        var parameters = member.Elements("parameter").Select(parameter => (Name: (string)parameter.Attribute("name")!,
            Type: (string)parameter.Attribute("type")!, String: (bool?)parameter.Attribute("string") == true)).ToArray();
        var returnType = (string)member.Attribute("return")!;
        var stringReturn = (bool?)member.Attribute("stringReturn") == true;
        var wrapper = stringReturn || parameters.Any(parameter => parameter.String);
        var nativeName = wrapper ? "__native_" + name : name;
        var signature = string.Join(", ", parameters.Select(parameter => $"{parameter.Type} @{parameter.Name}"));
        var declaration = emitImports
            ? $"[{InteropServices}DllImport({SymbolDisplay.FormatLiteral(module, true)}, EntryPoint = {SymbolDisplay.FormatLiteral(name, true)}, " +
                $"ExactSpelling = true, CallingConvention = {InteropServices}CallingConvention.{(convention == "Stdcall" ? "StdCall" : "Cdecl")})]\n" +
                $"{access} static unsafe extern {returnType} @{nativeName}({signature});"
            : $"{access} static unsafe {returnType} @{nativeName}({signature}) => " +
                "throw new global::System.PlatformNotSupportedException(\"Native imports are not available in this build configuration.\");";
        if (!wrapper)
            return declaration;

        var strings = parameters.Where(parameter => parameter.String).ToArray();
        var managedSignature = string.Join(", ", parameters.Select(parameter => $"{(parameter.String ? "string?" : parameter.Type)} @{parameter.Name}"));
        var call = $"@{nativeName}(" + string.Join(", ", parameters.Select(parameter => parameter.String
            ? $"(byte*)__utf8_{parameter.Name}" : $"@{parameter.Name}")) + ")";
        var result = stringReturn ? $"return {InteropServices}Marshal.PtrToStringUTF8((nint){call});" :
            returnType == "void" ? call + ";" : "return " + call + ";";
        var body = new StringBuilder();
        body.Append(declaration).Append('\n').Append(access).Append(" static unsafe ").Append(stringReturn ? "string?" : returnType)
            .Append(" @").Append(name).Append('(').Append(managedSignature).Append(")\n{\n");
        if (strings.Length == 0)
            return body.Append(result).Append("\n}").ToString();
        body.Append(string.Join("\n", strings.Select(parameter => $"nint __utf8_{parameter.Name} = 0;"))).Append("\ntry\n{\n");
        foreach (var parameter in strings)
            body.Append($"__utf8_{parameter.Name} = {InteropServices}Marshal.StringToCoTaskMemUTF8(@{parameter.Name});\n");
        body.Append(result).Append("\n}\nfinally\n{\n");
        foreach (var parameter in strings)
            body.Append($"{InteropServices}Marshal.FreeCoTaskMem(__utf8_{parameter.Name});\n");
        return body.Append("}\n}").ToString();
    }

    private static string RenderType(XElement type)
    {
        var name = (string)type.Attribute("name")!;
        if (type.Name == "enum")
            return $"public enum @{name} : {(string)type.Attribute("type")!} {{\n" +
                string.Join(",\n", type.Elements("item").Select(item => $"@{(string)item.Attribute("name")!} = {(string)item.Attribute("value")!}")) + "\n}";
        if (type.Name == "array")
        {
            var element = (string)type.Attribute("type")!;
            var length = (long)type.Attribute("length")!;
            return $"[{InteropServices}StructLayout({InteropServices}LayoutKind.Sequential)]\n" +
                $"public unsafe struct @{name} {{\n" +
                string.Join("\n", Enumerable.Range(0, (int)length).Select(index => $"public {element} element{index};")) +
                $"\npublic {element} this[long index] {{ get {{ if ((ulong)index >= {length}UL) throw new global::System.IndexOutOfRangeException(); fixed ({element}* data = &element0) return data[index]; }} " +
                $"set {{ if ((ulong)index >= {length}UL) throw new global::System.IndexOutOfRangeException(); fixed ({element}* data = &element0) data[index] = value; }} }}\n" +
                "}";
        }
        if ((bool?)type.Attribute("incomplete") == true)
            return $"public struct @{name} {{ }}";

        var sequential = (string?)type.Attribute("layout") == "sequential";
        var source = new StringBuilder($"[{InteropServices}StructLayout({InteropServices}LayoutKind.{(sequential ? "Sequential" : "Explicit")}, " +
            $"Size = {(string)type.Attribute("size")!}, Pack = {(string)type.Attribute("alignment")!})]\n" +
            $"public unsafe struct @{name} {{\n");
        foreach (var member in type.Elements())
        {
            var memberName = (string)member.Attribute("name")!;
            var memberType = (string)member.Attribute("type")!;
            if (member.Name == "field")
                source.Append(sequential ? "" : $"[{InteropServices}FieldOffset({(string)member.Attribute("offset")!})] ")
                    .Append("public ").Append(memberType).Append(" @").Append(memberName).Append(";\n");
            else if (member.Name == "bitfield")
            {
                var storage = $"ref {CompilerServices}Unsafe.As<{name}, byte>(ref this), {(string)member.Attribute("bitOffset")!}, {(string)member.Attribute("width")!}";
                source.Append($"public {memberType} @{memberName} {{ get => unchecked(({memberType})NativeBitfield.Read(" +
                    $"{storage}, {((bool?)member.Attribute("signed") == true ? "true" : "false")})); " +
                    $"set => NativeBitfield.Write({storage}, unchecked((ulong)value)); }}\n");
            }
            else if (member.Name == "flexible")
                source.Append($"public {memberType}* @{memberName} => ({memberType}*)((byte*){CompilerServices}Unsafe.AsPointer(ref this) + {(string)member.Attribute("offset")!});\n");
        }
        return source.Append('}').ToString();
    }

    private static string HeaderKey(string header, string? macros) => macros is null ? header : header + "?" + macros;

    private static string HintName(string displayName) =>
        string.Concat(displayName.Select(character => char.IsLetterOrDigit(character) || character == '.' ? character : '_'));

    private static void Error(SourceProductionContext output, LocationInfo? location, string message) =>
        output.ReportDiagnostic(Diagnostic.Create(InvalidBinding, location?.ToLocation(), message));

    private sealed class Manifest
    {
        public XElement? Root { get; private set; }
        public string TypeNamespace { get; private set; } = "";
        public string? Error { get; private set; }

        public static Manifest Parse(ImmutableArray<string> texts)
        {
            if (texts.Length != 1 || texts[0].Length == 0)
                return new Manifest { Error = "Build the project to create exactly one native binding manifest (*.nativebindings.xml)." };
            try
            {
                var root = XDocument.Parse(texts[0]).Root!;
                if ((string?)root.Attribute("schema") != "2")
                    return new Manifest { Error = "The native binding manifest uses an unsupported schema. Rebuild the project." };
                return new Manifest { Root = root, TypeNamespace = (string?)root.Attribute("typeNamespace") ?? "LightStudio.NativeBindings.Types" };
            }
            catch (XmlException exception)
            {
                return new Manifest { Error = $"Invalid native binding manifest: {exception.Message}" };
            }
        }
    }

    private sealed record Include(string Header, string? Class, int Access, string? ImportName, string? Macros);

    private sealed record LocationInfo(string Path, TextSpan Span, LinePositionSpan Lines)
    {
        public Location ToLocation() => Location.Create(Path, Span, Lines);

        public static LocationInfo? From(Location? location) => location is null || location.SourceTree is null
            ? null
            : new LocationInfo(location.SourceTree.FilePath, location.SourceSpan, location.GetLineSpan().Span);
    }

    private sealed record Target(string DisplayName, string Namespace, EquatableArray<string> Hierarchy, EquatableArray<Include> Includes,
        LocationInfo? Location, string? Error)
    {
        public static Target Create(INamedTypeSymbol symbol)
        {
            var hierarchy = new Stack<INamedTypeSymbol>();
            for (var current = symbol; current is not null; current = current.ContainingType)
                hierarchy.Push(current);
            string? error = null;
            if (hierarchy.Any(type => type.TypeKind is not (TypeKind.Class or TypeKind.Struct) || type.TypeParameters.Length != 0 ||
                type.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax() is not TypeDeclarationSyntax declaration ||
                    !declaration.Modifiers.Any(SyntaxKind.PartialKeyword))))
                error = "Include requires non-generic partial classes or structs, including all containing types.";
            var includes = symbol.GetAttributes().Where(attribute => attribute.AttributeClass?.ToDisplayString() == AttributeName).Select(attribute =>
            {
                string? Named(string name) => attribute.NamedArguments.FirstOrDefault(pair => pair.Key == name).Value.Value as string;
                var access = attribute.NamedArguments.FirstOrDefault(pair => pair.Key == "Access").Value.Value is int value ? value : 32;
                var macros = attribute.NamedArguments.FirstOrDefault(pair => pair.Key == "Macros").Value is { Kind: TypedConstantKind.Array } array
                    ? string.Join(" ", array.Values.Select(item => item.Value as string).Where(item => item is not null).Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal))
                    : null;
                return new Include(attribute.ConstructorArguments.FirstOrDefault().Value as string ?? "", Named("Class"), access, Named("ImportName"), macros);
            }).ToImmutableArray();
            var declarations = hierarchy.Select(type => AccessModifier(type.DeclaredAccessibility) + (type.IsStatic ? " static" : "") +
                (type.IsReadOnly ? " readonly" : "") + " partial " + (type.IsRecord ? "record " : "") +
                (type.TypeKind == TypeKind.Struct ? "struct @" : "class @") + type.Name).ToImmutableArray();
            return new Target(symbol.ToDisplayString(), symbol.ContainingNamespace.IsGlobalNamespace ? "" : symbol.ContainingNamespace.ToDisplayString(),
                new EquatableArray<string>(declarations), new EquatableArray<Include>(includes), LocationInfo.From(symbol.Locations.FirstOrDefault()), error);
        }

        private static string AccessModifier(Accessibility accessibility) => accessibility switch
        {
            Microsoft.CodeAnalysis.Accessibility.Public => "public",
            Microsoft.CodeAnalysis.Accessibility.Private => "private",
            Microsoft.CodeAnalysis.Accessibility.Protected => "protected",
            Microsoft.CodeAnalysis.Accessibility.ProtectedOrInternal => "protected internal",
            Microsoft.CodeAnalysis.Accessibility.ProtectedAndInternal => "private protected",
            _ => "internal"
        };
    }

    private readonly struct EquatableArray<T>(ImmutableArray<T> items) : IEquatable<EquatableArray<T>>, IEnumerable<T>
    {
        private readonly ImmutableArray<T> items = items;

        public bool Equals(EquatableArray<T> other) => items.IsDefaultOrEmpty ? other.items.IsDefaultOrEmpty : !other.items.IsDefault && items.SequenceEqual(other.items);

        public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

        public override int GetHashCode() => items.IsDefaultOrEmpty ? 0 : items.Aggregate(17, (hash, item) => hash * 31 + (item?.GetHashCode() ?? 0));

        public IEnumerator<T> GetEnumerator() => (items.IsDefault ? ImmutableArray<T>.Empty : items).AsEnumerable().GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
