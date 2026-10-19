using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using NativeBindings.Parser;

try
{
    switch (args)
    {
        case ["--options", var optionsPath]:
            await GenerateAsync(await Options.LoadAsync(optionsPath));
            break;
        case ["--link-inputs", "--options", var optionsPath]:
            var target = await NativeTarget.LoadAsync(await Options.LoadAsync(optionsPath));
            if (target.Backend != LinkBackend.Direct || target.LinkInputs.Count == 0)
                throw new InvalidOperationException($"{target.Runtime} ({target.Provider}) has no statically linked system libraries.");
            foreach (var input in target.LinkInputs)
                Console.WriteLine(input);
            break;
        default:
            throw new ArgumentException("Usage: NativeBindings.Parser [--link-inputs] --options <file>");
    }
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"error NB1001: {exception.Message.ReplaceLineEndings(" ")}");
    Console.Error.WriteLine(exception);
    return 1;
}

static async Task GenerateAsync(Options options)
{
    var output = Path.GetFullPath(options.Get("output"));
    var workDirectory = Path.GetDirectoryName(output)!;
    Directory.CreateDirectory(workDirectory);
    var sources = await File.ReadAllLinesAsync(options.Get("sources"));
    var requests = await RequestCollector.CollectAsync(sources.Where(line => line.Length != 0), options.List("defineConstants"));

    var seed = new StringBuilder()
        .Append(typeof(HeaderRequest).Assembly.ManifestModule.ModuleVersionId).Append('\n')
        .Append(options.Describe())
        .AppendJoin('\n', requests.Select(request => request.Key)).Append('\n');
    foreach (var variable in new[] { "PKG_CONFIG", "PKG_CONFIG_PATH", "PKG_CONFIG_LIBDIR", "PKG_CONFIG_SYSROOT_DIR",
        "ANDROID_HOME", "ANDROID_SDK_ROOT", "ANDROID_NDK_ROOT", "ANDROID_NDK_HOME" })
        seed.Append(variable).Append('=').Append(Environment.GetEnvironmentVariable(variable)).Append('\n');
    var fingerprintPath = output + ".fingerprint";
    if (File.Exists(output) && await IsCurrentAsync(fingerprintPath, seed.ToString()))
        return;

    XElement document;
    SortedSet<string> dependencies;
    if (requests.Count == 0)
    {
        document = new XElement("bindings");
        dependencies = [];
    }
    else
    {
        var target = await NativeTarget.LoadAsync(options);
        var exports = await ExportCatalog.LoadAsync(target);
        LibClang.Load(target.LibClangCandidates);
        if (Path.IsPathFullyQualified(LibClang.LoadedPath))
            target.DependencyFiles.Add(new FileInfo(LibClang.LoadedPath).ResolveLinkTarget(true)?.FullName ?? LibClang.LoadedPath);
        using var parser = new HeaderParser(target, exports, workDirectory);
        document = parser.Parse(requests);
        document.Add(new XAttribute("triple", parser.Triple), new XAttribute("pointerBits", parser.PointerBits),
            new XAttribute("provider", target.Provider), new XAttribute("linkage", target.Backend.ToString().ToLowerInvariant()));
        dependencies = target.DependencyFiles;
        Console.WriteLine($"Native bindings for {target.Runtime} ({target.Provider}, {target.Backend.ToString().ToLowerInvariant()} linkage, " +
            $"{Path.GetFileName(LibClang.LoadedPath)}): {requests.Count} requests, {document.Descendants("function").Count()} functions, " +
            $"{document.Descendants("constant").Count()} constants, {document.Element("types")!.Elements().Count()} types, " +
            $"{document.Descendants("excluded").Count()} exclusions.");
    }
    document = new XElement("bindings", new XAttribute("schema", 2), new XAttribute("runtime", options.Get("runtime")),
        new XAttribute("typeNamespace", options.Get("typeNamespace", "LightStudio.NativeBindings.Types")), document.Attributes(), document.Elements());
    var text = document.ToString();
    if (!File.Exists(output) || await File.ReadAllTextAsync(output) != text)
        await WriteAtomicallyAsync(output, text);
    await WriteAtomicallyAsync(fingerprintPath, Fingerprint(seed.ToString(), dependencies) + "\n" + string.Join('\n', dependencies));
}

// Dependencies record the headers, libraries, and package metadata that produced the manifest.
static async Task<bool> IsCurrentAsync(string fingerprintPath, string seed)
{
    if (!File.Exists(fingerprintPath))
        return false;
    var lines = await File.ReadAllLinesAsync(fingerprintPath);
    return lines.Length != 0 && lines[0] == Fingerprint(seed, lines.Skip(1).Where(line => line.Length != 0));
}

static string Fingerprint(string seed, IEnumerable<string> dependencies)
{
    var builder = new StringBuilder(seed);
    foreach (var dependency in dependencies)
    {
        var file = new FileInfo(dependency);
        builder.Append(dependency).Append('|').Append(file.Exists ? file.Length : -1).Append('|')
            .Append(file.Exists ? file.LastWriteTimeUtc.Ticks : 0).Append('\n');
    }
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
}

static async Task WriteAtomicallyAsync(string path, string text)
{
    var temporary = path + "." + Environment.ProcessId + ".tmp";
    await File.WriteAllTextAsync(temporary, text);
    File.Move(temporary, path, overwrite: true);
}
