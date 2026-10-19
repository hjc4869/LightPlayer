using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis.CSharp;

namespace NativeBindings.Parser;

internal enum TypeUse
{
    Parameter,
    Return,
    Field,
    Pointee,
}

/// <summary>Lowers C types of the selected target to blittable C# types and records the native type declarations they need.</summary>
internal sealed class TypeModel(NativeTarget target, int longBits, Func<ClangCursor, bool> owns)
{
    private readonly Dictionary<string, XElement> declarations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> owners = new(StringComparer.Ordinal);

    public IEnumerable<XElement> Declarations => declarations.Values.OrderBy(node => (string)node.Attribute("name")!, StringComparer.Ordinal);

    public bool HasName(ClangCursor declaration) => names.ContainsKey(LibClang.Text(LibClang.clang_getCursorUSR(declaration)));

    /// <summary>Names an anonymous record or enum after the typedef that declares it.</summary>
    public void RegisterTypedef(ClangCursor typedef)
    {
        var type = Unwrap(LibClang.clang_getTypedefDeclUnderlyingType(typedef), out _);
        if (type.Kind is not (TypeKind.Record or TypeKind.Enum))
            return;
        var declaration = LibClang.clang_getTypeDeclaration(type);
        var usr = LibClang.Text(LibClang.clang_getCursorUSR(declaration));
        var name = LibClang.Spelling(typedef);
        if (usr.Length != 0 && !IsIdentifier(LibClang.Spelling(declaration)) && IsIdentifier(name) && !names.ContainsKey(usr) && !owners.ContainsKey(name))
            Assign(usr, name);
    }

    public string Lower(ClangType type, TypeUse use, string? suggestedName = null)
    {
        type = Unwrap(type, out var special);
        if (special is not null)
            return special;
        var canonical = LibClang.clang_getCanonicalType(type);
        switch (canonical.Kind)
        {
            case TypeKind.Void:
                return "void";
            case TypeKind.Bool or TypeKind.CharU or TypeKind.UChar or TypeKind.CharS:
                return "byte";
            case TypeKind.SChar:
                return "sbyte";
            case TypeKind.Short or TypeKind.UShort or TypeKind.Int or TypeKind.UInt or TypeKind.Long or TypeKind.ULong or
                TypeKind.LongLong or TypeKind.ULongLong or TypeKind.WChar or TypeKind.Char16 or TypeKind.Char32:
                return Integer(canonical);
            case TypeKind.Float:
                return "float";
            case TypeKind.Double:
                return "double";
            case TypeKind.LongDouble when LibClang.clang_Type_getSizeOf(canonical) == 8:
                return "double";
            case TypeKind.Pointer:
                return LowerPointer(LibClang.clang_getPointeeType(type.Kind == TypeKind.Pointer ? type : canonical), use, suggestedName);
            case TypeKind.Record:
            {
                var name = Record(LibClang.clang_getTypeDeclaration(canonical), suggestedName);
                if (use is TypeUse.Parameter or TypeUse.Return && (string?)declarations[name].Attribute("layout") != "sequential")
                    throw new NotSupportedException($"By-value record {name} has no verified calling ABI");
                return name;
            }
            case TypeKind.Enum:
                return Enum(LibClang.clang_getTypeDeclaration(canonical), suggestedName);
            case TypeKind.ConstantArray when use is TypeUse.Field or TypeUse.Pointee:
                return Array(Lower(ElementType(type, canonical), TypeUse.Field, suggestedName), LibClang.clang_getArraySize(canonical));
            case TypeKind.ConstantArray or TypeKind.IncompleteArray:
                return Lower(ElementType(type, canonical), TypeUse.Pointee, suggestedName) + "*";
            default:
                throw new NotSupportedException($"Unsupported native type {LibClang.Spelling(type)} ({canonical.Kind})");
        }
    }

    public string LowerParameter(ClangType type)
    {
        if (IsVaList(type))
        {
            var canonical = LibClang.clang_getCanonicalType(type);
            // AArch64 SysV passes the 32-byte va_list record indirectly, like the pointer forms on other targets.
            if (canonical.Kind is TypeKind.Pointer or TypeKind.ConstantArray ||
                canonical.Kind == TypeKind.Record && target.Architecture == "arm64" && LibClang.clang_Type_getSizeOf(canonical) > 16)
                return "void*";
            throw new NotSupportedException("va_list is passed by value on this target");
        }
        var unwrapped = LibClang.clang_getCanonicalType(type);
        if (unwrapped.Kind == TypeKind.Pointer &&
            LibClang.clang_getCanonicalType(LibClang.clang_getPointeeType(unwrapped)).Kind is TypeKind.FunctionProto or TypeKind.FunctionNoProto)
            return "void*";
        return Lower(type, TypeUse.Parameter);
    }

    /// <summary>Returns the C# calling convention of a function type, or null when the convention is not the target's C convention.</summary>
    public string? CallingConvention(ClangType function) => LibClang.clang_getFunctionTypeCallingConv(function) switch
    {
        0 or 1 => "Cdecl",
        2 when target.Architecture == "x86" => "Stdcall",
        6 or 7 when target.Architecture == "arm" => "Cdecl",
        10 when target.IsWindows && target.Architecture == "x64" => "Cdecl",
        11 when !target.IsWindows && target.Architecture == "x64" => "Cdecl",
        _ => null
    };

    public string Integer(ClangType canonical)
    {
        var size = LibClang.clang_Type_getSizeOf(canonical);
        var unsigned = canonical.Kind is TypeKind.Bool or TypeKind.CharU or TypeKind.UChar or TypeKind.UShort or TypeKind.UInt or
            TypeKind.ULong or TypeKind.ULongLong or TypeKind.Char16 or TypeKind.Char32 || canonical.Kind == TypeKind.WChar && size == 2;
        if (canonical.Kind is TypeKind.Long or TypeKind.ULong && size * 8 != longBits)
            throw new InvalidOperationException($"C long is {size * 8} bits, but the target reports {longBits}.");
        return (size, unsigned) switch
        {
            (1, false) => "sbyte",
            (1, true) => "byte",
            (2, false) => "short",
            (2, true) => "ushort",
            (4, false) => "int",
            (4, true) => "uint",
            (8, false) => "long",
            (8, true) => "ulong",
            _ => throw new NotSupportedException($"Unsupported {size}-byte integer {LibClang.Spelling(canonical)}")
        };
    }

    public static string IntegerLiteral(string type, ulong bits) =>
        $"unchecked(({type})0x{bits.ToString("X", CultureInfo.InvariantCulture)}UL)";

    public string Enum(ClangCursor declaration, string? suggestedName = null)
    {
        var name = Name(declaration, suggestedName, "enum");
        if (declarations.ContainsKey(name))
            return name;
        var underlyingType = LibClang.clang_getCanonicalType(LibClang.clang_getEnumDeclIntegerType(declaration));
        var underlying = Integer(underlyingType);
        var node = new XElement("enum", new XAttribute("name", name), new XAttribute("type", underlying));
        foreach (var item in LibClang.Children(declaration).Where(item => item.Kind == CursorKind.EnumConstantDecl))
            node.Add(new XElement("item", new XAttribute("name", LibClang.Spelling(item)),
                new XAttribute("value", IntegerLiteral(underlying, EnumValue(item, underlying)))));
        declarations[name] = node;
        return name;
    }

    public static ulong EnumValue(ClangCursor item, string underlying) => underlying.StartsWith('u') || underlying == "byte"
        ? LibClang.clang_getEnumConstantDeclUnsignedValue(item)
        : unchecked((ulong)LibClang.clang_getEnumConstantDeclValue(item));

    public string Record(ClangCursor declaration, string? suggestedName = null)
    {
        var definition = LibClang.clang_getCursorDefinition(declaration);
        if (LibClang.clang_Cursor_isNull(definition) != 0)
            return Opaque(declaration, suggestedName);
        var name = Name(definition, suggestedName, "record");
        if (declarations.TryGetValue(name, out var existing) && (bool?)existing.Attribute("incomplete") != true)
            return name;
        var type = LibClang.clang_getCursorType(definition);
        var size = LibClang.clang_Type_getSizeOf(type);
        var alignment = LibClang.clang_Type_getAlignOf(type);
        if (size < 0 || alignment <= 0)
            return Opaque(declaration, suggestedName);

        var union = definition.Kind == CursorKind.UnionDecl;
        var node = new XElement("record", new XAttribute("name", name), new XAttribute("kind", union ? "union" : "struct"),
            new XAttribute("size", size), new XAttribute("alignment", alignment));
        declarations[name] = node;
        var scalar = new List<(long Offset, long Size, long Alignment)>();
        bool sequential;
        try
        {
            sequential = !union & AddFields(node, type, 0, name, scalar);
        }
        catch
        {
            if (existing is null)
                declarations.Remove(name);
            else
                declarations[name] = existing;
            throw;
        }
        if (sequential)
        {
            long offset = 0, largest = 1;
            foreach (var field in scalar)
            {
                offset = Align(offset, field.Alignment);
                sequential &= offset == field.Offset;
                offset += field.Size;
                largest = Math.Max(largest, field.Alignment);
            }
            sequential &= scalar.Count != 0 && largest == alignment && Align(offset, alignment) == size;
        }
        node.Add(new XAttribute("layout", sequential ? "sequential" : "explicit"));
        return name;
    }

    // Returns whether every member is a plain scalar field, which permits a sequential layout check.
    private bool AddFields(XElement node, ClangType record, long baseBits, string owner, List<(long, long, long)> scalar)
    {
        var plain = true;
        foreach (var field in LibClang.Fields(record))
        {
            var name = LibClang.Spelling(field);
            if (name == owner)
                name += "_";
            var fieldType = LibClang.clang_getCursorType(field);
            var canonical = LibClang.clang_getCanonicalType(fieldType);
            var offsetBits = baseBits + LibClang.clang_Cursor_getOffsetOfField(field);
            if (LibClang.clang_Cursor_isBitField(field) != 0)
            {
                plain = false;
                var width = LibClang.clang_getFieldDeclBitWidth(field);
                if (name.Length == 0 || width <= 0)
                    continue;
                var bitType = Lower(fieldType, TypeUse.Field, owner + "_" + name);
                node.Add(new XElement("bitfield", new XAttribute("name", name), new XAttribute("bitOffset", offsetBits),
                    new XAttribute("width", width), new XAttribute("type", bitType),
                    new XAttribute("signed", IsSigned(canonical))));
                continue;
            }
            if (canonical.Kind == TypeKind.Record && LibClang.clang_Cursor_isAnonymousRecordDecl(LibClang.clang_getTypeDeclaration(canonical)) != 0)
            {
                plain = false;
                AddFields(node, canonical, offsetBits, owner, []);
                continue;
            }
            if (name.Length == 0)
                continue;
            if (offsetBits % 8 != 0)
                throw new NotSupportedException($"Field {owner}.{name} is not byte aligned");
            var offset = offsetBits / 8;
            if (canonical.Kind == TypeKind.IncompleteArray || canonical.Kind == TypeKind.ConstantArray && LibClang.clang_getArraySize(canonical) == 0)
            {
                plain = false;
                node.Add(new XElement("flexible", new XAttribute("name", name), new XAttribute("offset", offset),
                    new XAttribute("type", Lower(ElementType(Unwrap(fieldType, out _), canonical), TypeUse.Field, owner + "_" + name))));
                continue;
            }
            string type;
            try
            {
                type = Lower(fieldType, TypeUse.Field, owner + "_" + name);
            }
            catch (NotSupportedException exception)
            {
                // Keep the record usable: an unrepresentable member becomes raw storage of the same size.
                plain = false;
                node.Add(new XElement("field", new XAttribute("name", name), new XAttribute("offset", offset),
                    new XAttribute("type", Array("byte", LibClang.clang_Type_getSizeOf(fieldType))), new XAttribute("opaque", exception.Message)));
                continue;
            }
            node.Add(new XElement("field", new XAttribute("name", name), new XAttribute("offset", offset), new XAttribute("type", type)));
            if (IsScalar(canonical))
                scalar.Add((offset, LibClang.clang_Type_getSizeOf(canonical), LibClang.clang_Type_getAlignOf(canonical)));
            else
                plain = false;
        }
        return plain;
    }

    private string LowerPointer(ClangType pointee, TypeUse use, string? suggestedName)
    {
        var canonical = LibClang.clang_getCanonicalType(pointee);
        switch (canonical.Kind)
        {
            case TypeKind.FunctionProto or TypeKind.FunctionNoProto:
                return use == TypeUse.Parameter ? "void*" : FunctionPointer(pointee, canonical) ?? "void*";
            case TypeKind.Void:
                return "void*";
            case TypeKind.Record:
            {
                var declaration = LibClang.clang_getTypeDeclaration(canonical);
                if (!owns(declaration))
                    return Opaque(declaration, suggestedName) + "*";
                try
                {
                    return Record(declaration, suggestedName) + "*";
                }
                catch (NotSupportedException)
                {
                    return Opaque(declaration, suggestedName) + "*";
                }
            }
            default:
                return Lower(pointee, TypeUse.Pointee, suggestedName) + "*";
        }
    }

    // Argument and result types come from the type as written, so typedefs such as size_t survive.
    private string? FunctionPointer(ClangType function, ClangType canonical)
    {
        if (canonical.Kind != TypeKind.FunctionProto || LibClang.clang_isFunctionTypeVariadic(canonical) != 0 ||
            CallingConvention(canonical) is not { } convention)
            return null;
        try
        {
            var parameters = Enumerable.Range(0, LibClang.clang_getNumArgTypes(function))
                .Select(index => LowerParameter(LibClang.clang_getArgType(function, (uint)index)));
            return $"delegate* unmanaged[{convention}]<" +
                string.Join(", ", parameters.Append(Lower(LibClang.clang_getResultType(function), TypeUse.Return))) + ">";
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private string Opaque(ClangCursor declaration, string? suggestedName)
    {
        var name = Name(declaration, suggestedName, "record");
        if (!declarations.ContainsKey(name))
            declarations[name] = new XElement("record", new XAttribute("name", name), new XAttribute("incomplete", true));
        return name;
    }

    private string Array(string element, long length)
    {
        if (length <= 0)
            throw new NotSupportedException("Zero-length native array");
        var name = "NativeArray_" + string.Concat(element.Select(character => char.IsAsciiLetterOrDigit(character) ? character : '_')) + "_" +
            length.ToString(CultureInfo.InvariantCulture);
        if (!declarations.ContainsKey(name))
            declarations[name] = new XElement("array", new XAttribute("name", name), new XAttribute("type", element), new XAttribute("length", length));
        return name;
    }

    private string Name(ClangCursor declaration, string? suggestedName, string kind)
    {
        var usr = LibClang.Text(LibClang.clang_getCursorUSR(declaration));
        if (usr.Length == 0)
            throw new NotSupportedException("Native declaration has no stable identity");
        if (names.TryGetValue(usr, out var name))
            return name;
        name = LibClang.Spelling(declaration);
        if (!IsIdentifier(name))
            name = suggestedName is not null && IsIdentifier(suggestedName) && !owners.ContainsKey(suggestedName)
                ? suggestedName
                : $"Native_Anonymous_{kind}_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(usr)))[..12];
        if (owners.TryGetValue(name, out var owner) && owner != usr)
            throw new InvalidOperationException($"Distinct native declarations share the name {name}: {owner} and {usr}.");
        Assign(usr, name);
        return name;
    }

    private void Assign(string usr, string name)
    {
        names[usr] = name;
        owners[name] = usr;
    }

    private static ClangType ElementType(ClangType type, ClangType canonical) =>
        LibClang.clang_getArrayElementType(type.Kind is TypeKind.ConstantArray or TypeKind.IncompleteArray ? type : canonical);

    private static ClangType Unwrap(ClangType type, out string? special)
    {
        special = null;
        while (true)
        {
            switch (type.Kind)
            {
                case TypeKind.Typedef:
                    special = LibClang.Text(LibClang.clang_getTypedefName(type)) switch
                    {
                        "size_t" or "uintptr_t" => "nuint",
                        "ptrdiff_t" or "intptr_t" or "ssize_t" => "nint",
                        _ => null
                    };
                    if (special is not null)
                        return type;
                    type = LibClang.clang_getTypedefDeclUnderlyingType(LibClang.clang_getTypeDeclaration(type));
                    continue;
                case TypeKind.Elaborated:
                    type = LibClang.clang_Type_getNamedType(type);
                    continue;
                case TypeKind.Attributed:
                    type = LibClang.clang_Type_getModifiedType(type);
                    continue;
                default:
                    return type;
            }
        }
    }

    private static bool IsVaList(ClangType type)
    {
        while (true)
        {
            if (type.Kind == TypeKind.Typedef)
            {
                if (LibClang.Text(LibClang.clang_getTypedefName(type)) is "va_list" or "__builtin_va_list" or "__gnuc_va_list")
                    return true;
                type = LibClang.clang_getTypedefDeclUnderlyingType(LibClang.clang_getTypeDeclaration(type));
            }
            else if (type.Kind == TypeKind.Elaborated)
                type = LibClang.clang_Type_getNamedType(type);
            else if (type.Kind == TypeKind.Attributed)
                type = LibClang.clang_Type_getModifiedType(type);
            else
                return false;
        }
    }

    private static bool IsScalar(ClangType canonical) => canonical.Kind is TypeKind.Bool or TypeKind.CharU or TypeKind.UChar or TypeKind.CharS or
        TypeKind.SChar or TypeKind.Short or TypeKind.UShort or TypeKind.Int or TypeKind.UInt or TypeKind.Long or TypeKind.ULong or
        TypeKind.LongLong or TypeKind.ULongLong or TypeKind.WChar or TypeKind.Char16 or TypeKind.Char32 or TypeKind.Float or
        TypeKind.Double or TypeKind.Pointer or TypeKind.Enum;

    private static bool IsSigned(ClangType canonical)
    {
        if (canonical.Kind == TypeKind.Enum)
            canonical = LibClang.clang_getCanonicalType(LibClang.clang_getEnumDeclIntegerType(LibClang.clang_getTypeDeclaration(canonical)));
        return canonical.Kind is TypeKind.CharS or TypeKind.SChar or TypeKind.Short or TypeKind.Int or TypeKind.Long or TypeKind.LongLong ||
            canonical.Kind == TypeKind.WChar && LibClang.clang_Type_getSizeOf(canonical) == 4;
    }

    private static bool IsIdentifier(string name) => SyntaxFacts.IsValidIdentifier(name) && !name.StartsWith('@');

    private static long Align(long value, long alignment) => (value + alignment - 1) / alignment * alignment;
}
