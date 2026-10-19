using System.Buffers.Binary;
using System.Text;

namespace NativeBindings.Parser;

/// <summary>Reads the dynamic identity and default-version function exports of a little-endian ELF shared library.</summary>
internal static class ElfReader
{
    private const uint DynamicSymbols = 11, Dynamic = 6, VersionSymbols = 0x6fffffff;

    public static (ushort Machine, string? SoName, List<string> Functions) Read(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 64 || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x464c457f)
            throw new BadImageFormatException($"Not an ELF file: {path}");
        var is64 = data[4] switch
        {
            1 => false,
            2 => true,
            _ => throw new BadImageFormatException($"Unknown ELF class: {path}")
        };
        if (data[5] != 1)
            throw new BadImageFormatException($"Only little-endian ELF libraries are supported: {path}");
        var machine = U16(data, 18);
        var sectionOffset = is64 ? (long)U64(data, 40) : U32(data, 32);
        var sectionSize = U16(data, is64 ? 58 : 46);
        var sectionCount = U16(data, is64 ? 60 : 48);
        if (sectionOffset == 0 || sectionCount == 0)
            throw new BadImageFormatException($"ELF library has no section headers: {path}");

        (uint Type, long Offset, long Size, uint Link, long EntrySize) Section(int index)
        {
            var header = checked((int)(sectionOffset + (long)index * sectionSize));
            return is64
                ? (U32(data, header + 4), (long)U64(data, header + 24), (long)U64(data, header + 32), U32(data, header + 40), (long)U64(data, header + 56))
                : (U32(data, header + 4), U32(data, header + 16), U32(data, header + 20), U32(data, header + 24), U32(data, header + 36));
        }

        string Text(long tableOffset, uint offset)
        {
            var start = checked((int)(tableOffset + offset));
            var end = Array.IndexOf(data, (byte)0, start);
            return Encoding.UTF8.GetString(data, start, end - start);
        }

        var sections = Enumerable.Range(0, sectionCount).Select(Section).ToArray();
        var symbols = sections.FirstOrDefault(section => section.Type == DynamicSymbols);
        if (symbols.Type == 0)
            throw new BadImageFormatException($"ELF library has no dynamic symbol table: {path}");
        var strings = sections[symbols.Link].Offset;
        var versions = sections.FirstOrDefault(section => section.Type == VersionSymbols);

        string? soName = null;
        var dynamic = sections.FirstOrDefault(section => section.Type == Dynamic);
        if (dynamic.Type != 0)
        {
            var dynamicStrings = sections[dynamic.Link].Offset;
            var entrySize = is64 ? 16 : 8;
            for (var entry = dynamic.Offset; entry + entrySize <= dynamic.Offset + dynamic.Size; entry += entrySize)
            {
                var tag = is64 ? (long)U64(data, (int)entry) : (int)U32(data, (int)entry);
                if (tag == 0)
                    break;
                if (tag == 14)
                    soName = Text(dynamicStrings, (uint)(is64 ? U64(data, (int)entry + 8) : U32(data, (int)entry + 4)));
            }
        }

        var functions = new List<string>();
        var symbolSize = symbols.EntrySize != 0 ? symbols.EntrySize : is64 ? 24 : 16;
        for (var index = 1L; index < symbols.Size / symbolSize; index++)
        {
            var symbol = (int)(symbols.Offset + index * symbolSize);
            var info = data[symbol + (is64 ? 4 : 12)];
            var other = data[symbol + (is64 ? 5 : 13)];
            var sectionIndex = U16(data, symbol + (is64 ? 6 : 14));
            var binding = info >> 4;
            var type = info & 0xf;
            var visibility = other & 3;
            if (sectionIndex == 0 || binding is not (1 or 2 or 10) || type is not (2 or 10) || visibility is not (0 or 3))
                continue;
            if (versions.Type != 0 && (U16(data, (int)(versions.Offset + index * 2)) & 0x8000) != 0)
                continue;
            functions.Add(Text(strings, U32(data, symbol)));
        }
        return (machine, soName, functions);
    }

    public static ushort MachineFor(string architecture) => architecture switch
    {
        "x64" => 62,
        "arm64" => 183,
        "x86" => 3,
        "arm" => 40,
        "riscv64" => 243,
        "loongarch64" => 258,
        _ => throw new NotSupportedException($"Unsupported ELF architecture: {architecture}")
    };

    private static ushort U16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
    private static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
    private static ulong U64(byte[] data, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset));
}
