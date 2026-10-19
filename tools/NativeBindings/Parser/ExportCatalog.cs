using System.Reflection.PortableExecutable;

namespace NativeBindings.Parser;

/// <summary>Maps exported function symbols of the selected native assets to their import module names.</summary>
internal sealed class ExportCatalog
{
    private readonly Dictionary<string, HashSet<string>> providers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> modules = new(StringComparer.Ordinal);

    public static async Task<ExportCatalog> LoadAsync(NativeTarget target)
    {
        var catalog = new ExportCatalog();
        if (target.Libraries.Count == 0)
            throw new InvalidOperationException($"No native libraries were selected for {target.Runtime} ({target.Provider}).");
        foreach (var library in target.Libraries.DistinctBy(library => library.Path).OrderBy(library => library.Path, StringComparer.Ordinal))
        {
            target.DependencyFiles.Add(library.Path);
            if (library.Archive)
            {
                var module = target.Backend == LinkBackend.Wasm ? ArchiveStem(library.Path) : target.ImportToken;
                catalog.Add(await ArchiveSymbolsAsync(library.Path, target), library.Path, module);
            }
            else if (target.IsWindows)
                catalog.AddPortableExecutable(library, target);
            else if (target.IsApple)
                await catalog.AddMachOAsync(library, target);
            else
                catalog.AddElf(library, target);
        }
        if (catalog.providers.Count == 0)
            throw new InvalidOperationException($"The selected native libraries export no functions ({target.Provider}).");
        return catalog;
    }

    /// <summary>Returns the import module of the single provider of <paramref name="symbol"/>, or null when no selected library exports it.</summary>
    public string? Resolve(string symbol)
    {
        if (!providers.TryGetValue(symbol, out var candidates))
            return null;
        if (candidates.Count != 1)
            throw new InvalidOperationException($"Ambiguous native export {symbol}: {string.Join(", ", candidates.Order(StringComparer.Ordinal))}");
        return modules[candidates.Single()];
    }

    private void Add(IEnumerable<string> symbols, string provider, string module)
    {
        modules[provider] = module;
        foreach (var symbol in symbols)
        {
            if (!providers.TryGetValue(symbol, out var candidates))
                providers.Add(symbol, candidates = new HashSet<string>(StringComparer.Ordinal));
            candidates.Add(provider);
        }
    }

    private static string ArchiveStem(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".a", StringComparison.Ordinal) ? name[..^2] : Path.GetFileNameWithoutExtension(name);
    }

    private static async Task<IEnumerable<string>> ArchiveSymbolsAsync(string path, NativeTarget target)
    {
        if (target.SymbolTool.Length == 0)
            throw new InvalidOperationException($"Static bindings need a symbol tool (nm or llvm-nm) for {path}; set NativeBindingsSymbolTool.");
        var output = target.IsApple
            ? (await ProcessRunner.RunAsync(target.SymbolTool, ["-arch", AppleArchitecture(target), "-g", "-U", "-P", path])).Output
            : (await ProcessRunner.RunAsync(target.SymbolTool, ["--defined-only", "--extern-only", "--format=posix", path])).Output;
        return Symbols(output, target.IsApple);
    }

    private static IEnumerable<string> Symbols(string output, bool machO)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || fields[1] is not ("T" or "W" or "i" or "I"))
                continue;
            var symbol = fields[0];
            if (machO)
            {
                if (!symbol.StartsWith('_') || fields[1] == "I")
                    continue;
                symbol = symbol[1..];
            }
            var version = symbol.IndexOf('@');
            if (version >= 0)
            {
                if (!symbol.AsSpan(version).StartsWith("@@", StringComparison.Ordinal))
                    continue;
                symbol = symbol[..version];
            }
            yield return symbol;
        }
    }

    private static string AppleArchitecture(NativeTarget target) => target.Architecture == "arm64" ? "arm64" : "x86_64";

    private void AddElf(LibraryAsset library, NativeTarget target)
    {
        var (machine, soName, functions) = ElfReader.Read(library.Path);
        if (machine != ElfReader.MachineFor(target.Architecture))
            throw new InvalidOperationException($"{library.Path} is ELF machine {machine}, not {target.Runtime}.");
        var identity = soName ?? Path.GetFileName(library.Path);
        if (library.Identity is { Length: > 0 } expected && expected != identity)
            throw new InvalidOperationException($"ELF SONAME '{identity}' disagrees with the declared runtime identity '{expected}': {library.Path}");
        Add(functions, identity, identity);
    }

    private async Task AddMachOAsync(LibraryAsset library, NativeTarget target)
    {
        var identity = await ProcessRunner.RunAsync("xcrun", ["otool", "-arch", AppleArchitecture(target), "-D", library.Path]);
        var installName = identity.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).SingleOrDefault()?.Trim();
        if (string.IsNullOrEmpty(installName))
            throw new InvalidOperationException($"Library has no Mach-O install name for {AppleArchitecture(target)}: {library.Path}");
        var module = Path.GetFileName(installName);
        var symbols = await ProcessRunner.RunAsync(target.SymbolTool.Length != 0 ? target.SymbolTool : "nm",
            ["-arch", AppleArchitecture(target), "-g", "-U", "-P", library.Path]);
        Add(Symbols(symbols.Output, true), module, module);
    }

    private void AddPortableExecutable(LibraryAsset library, NativeTarget target)
    {
        using var stream = File.OpenRead(library.Path);
        using var image = new PEReader(stream);
        var expectedMachine = target.Architecture switch
        {
            "x64" => Machine.Amd64,
            "arm64" => Machine.Arm64,
            "x86" => Machine.I386,
            _ => throw new NotSupportedException($"Unsupported Windows runtime: {target.Runtime}")
        };
        if (image.PEHeaders.CoffHeader.Machine != expectedMachine || image.PEHeaders.PEHeader is not { } header)
            throw new InvalidOperationException($"Expected a {target.Runtime} PE library: {library.Path}");
        var exports = header.ExportTableDirectory;
        if (exports.Size == 0)
            throw new InvalidOperationException($"Library has no PE export directory: {library.Path}");
        var reader = image.GetSectionData(exports.RelativeVirtualAddress).GetReader();
        reader.Offset = 12;
        var identity = ReadExportName(image, reader.ReadInt32());
        if (library.Identity is { Length: > 0 } expected && !identity.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"PE export identity '{identity}' disagrees with the declared runtime identity '{expected}': {library.Path}");
        reader.ReadUInt32();
        var functionCount = reader.ReadUInt32();
        var nameCount = reader.ReadUInt32();
        var functions = image.GetSectionData(reader.ReadInt32()).GetReader();
        var names = image.GetSectionData(reader.ReadInt32()).GetReader();
        var ordinals = image.GetSectionData(reader.ReadInt32()).GetReader();
        var symbols = new List<string>();
        for (uint index = 0; index < nameCount; index++)
        {
            var name = ReadExportName(image, names.ReadInt32());
            var ordinal = ordinals.ReadUInt16();
            if (ordinal >= functionCount)
                throw new BadImageFormatException($"Invalid PE export ordinal for {name}: {library.Path}");
            functions.Offset = ordinal * sizeof(int);
            var address = functions.ReadInt32();
            if (address == 0)
                continue;
            if (address >= exports.RelativeVirtualAddress && (long)address < (long)exports.RelativeVirtualAddress + exports.Size)
                throw new NotSupportedException($"Forwarded PE export {name} requires provider resolution: {library.Path}");
            symbols.Add(name);
        }
        Add(symbols, identity, identity);
    }

    private static string ReadExportName(PEReader image, int address)
    {
        var reader = image.GetSectionData(address).GetReader();
        while (reader.RemainingBytes > 0)
        {
            if (reader.ReadByte() != 0)
                continue;
            var length = reader.Offset - 1;
            if (length == 0)
                break;
            reader.Offset = 0;
            return reader.ReadUTF8(length);
        }
        throw new BadImageFormatException("PE export name is empty or unterminated.");
    }
}
