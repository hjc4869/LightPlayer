using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace NativeBindings.Parser;

internal enum LinkBackend
{
    /// <summary>Imports name the runtime identity of each shared library.</summary>
    Dynamic,
    /// <summary>NativeAOT direct calls into statically linked archives through one import token.</summary>
    Direct,
    /// <summary>WebAssembly native linking; imports name the archive stem registered by the SDK.</summary>
    Wasm,
}

internal sealed record LibraryAsset(string Path, bool Archive, string? Identity, string? Name = null);

/// <summary>The selected native ABI, headers, libraries, and toolchain for one binding manifest.</summary>
internal sealed class NativeTarget
{
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private NativeTarget(string runtime) => Runtime = runtime;

    public string Runtime { get; }
    public string Architecture => Runtime[(Runtime.LastIndexOf('-') + 1)..];
    public bool IsWindows => Runtime.StartsWith("win-", StringComparison.Ordinal);
    public bool IsApple => Runtime.StartsWith("osx-", StringComparison.Ordinal) || Runtime.StartsWith("ios", StringComparison.Ordinal);
    public bool IsAndroid => Runtime.StartsWith("android-", StringComparison.Ordinal);
    public bool IsBrowser => Runtime == "browser-wasm";

    /// <summary>The compiler target; empty selects the host compiler's own default target.</summary>
    public string Triple { get; private set; } = "";
    public int ExpectedPointerBits { get; private set; }
    public int ExpectedLongBits { get; private set; }
    public LinkBackend Backend { get; private set; }
    public string ImportToken { get; private set; } = "";
    public string Provider { get; private set; } = "";
    public string Sysroot { get; private set; } = "";
    public string Clang { get; private set; } = "";
    public string SymbolTool { get; private set; } = "";
    public List<string> IncludeDirectories { get; } = [];
    /// <summary>Include roots whose whole tree belongs to the selected libraries unless the compiler treats them as system directories.</summary>
    public List<string> LibraryIncludeRoots { get; } = [];
    public List<string> Defines { get; } = [];
    public List<string> Undefines { get; } = [];
    public List<string> Arguments { get; } = [];
    public List<LibraryAsset> Libraries { get; } = [];
    /// <summary>Ordered native linker inputs for statically linked system libraries.</summary>
    public List<string> LinkInputs { get; } = [];
    public List<string> LibClangCandidates { get; } = [];
    public SortedSet<string> DependencyFiles { get; } = new(StringComparer.Ordinal);

    public static async Task<NativeTarget> LoadAsync(Options options)
    {
        var target = new NativeTarget(options.Get("runtime", RuntimeInformation.RuntimeIdentifier))
        {
            Clang = options.Get("clang"),
            SymbolTool = options.Get("symbolTool"),
            Sysroot = options.Get("sysroot"),
        };
        var isStatic = options.Flag("static");
        target.Backend = !isStatic ? LinkBackend.Dynamic : target.IsBrowser ? LinkBackend.Wasm : LinkBackend.Direct;
        target.Arguments.AddRange(options.List("compilerArguments"));
        if (options.Get("packageDirectory").Length != 0)
            await target.LoadPackageAsync(options);
        else
            await target.LoadSystemAsync(options);
        if (target.Backend == LinkBackend.Direct && target.ImportToken.Length == 0)
            target.ImportToken = options.Get("importName", "__Internal");
        await target.ResolveLibClangAsync(options.Get("libClang"));
        return target;
    }

    public IEnumerable<string> CompilerArguments()
    {
        yield return "-x";
        yield return "c";
        yield return "-std=gnu11";
        if (Triple.Length != 0)
        {
            yield return "-target";
            yield return Triple;
        }
        if (Sysroot.Length != 0)
        {
            if (IsApple)
            {
                yield return "-isysroot";
                yield return Sysroot;
            }
            else
                yield return "--sysroot=" + Sysroot;
        }
        foreach (var directory in IncludeDirectories)
        {
            yield return "-I";
            yield return directory;
        }
        foreach (var define in Defines)
            yield return "-D" + define;
        foreach (var undefine in Undefines)
            yield return "-U" + undefine;
        foreach (var argument in Arguments)
            yield return argument;
    }

    private async Task LoadPackageAsync(Options options)
    {
        var package = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Get("packageDirectory")));
        var manifestPath = Path.Combine(package, "native-bindings", "manifest.json");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("The native package has no binding profiles (native-bindings/manifest.json). " +
                "Restore a package that provides them or configure NativeBindingsPackageDirectory.", manifestPath);
        DependencyFiles.Add(manifestPath);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        var profiles = document.RootElement.GetProperty("profiles");
        var role = Backend == LinkBackend.Dynamic ? "shared" : "implementation-archive";
        bool Matches(JsonElement profile) =>
            profile.GetProperty("abi").GetProperty("runtimeIdentifier").GetString() == Runtime &&
            profile.GetProperty("libraries").EnumerateArray().Any(library => library.GetProperty("role").GetString() == role);

        var profileName = options.Get("profile");
        if (profileName.Length == 0)
        {
            var matches = profiles.EnumerateObject().Where(profile => Matches(profile.Value)).Select(profile => profile.Name).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException((matches.Length == 0
                    ? $"The native package has no {(Backend == LinkBackend.Dynamic ? "shared" : "static")} library profile for {Runtime}."
                    : $"Several native profiles match {Runtime}: {string.Join(", ", matches)}.") +
                    " Set NativeBindingsProfile to one of: " + string.Join(", ", profiles.EnumerateObject().Select(profile => profile.Name)));
            profileName = matches[0];
        }
        if (!profiles.TryGetProperty(profileName, out var selected))
            throw new InvalidOperationException($"Native profile '{profileName}' is unavailable. Available profiles: " +
                string.Join(", ", profiles.EnumerateObject().Select(profile => profile.Name)));
        if (!Matches(selected))
            throw new InvalidOperationException($"Native profile '{profileName}' does not provide {role} libraries for {Runtime}.");

        var abi = selected.GetProperty("abi");
        if (abi.GetProperty("endianness").GetString() != "little")
            throw new NotSupportedException("Only little-endian native profiles are supported.");
        Triple = abi.GetProperty("triple").GetString()!;
        ExpectedPointerBits = abi.GetProperty("pointerSize").GetInt32() * 8;
        ExpectedLongBits = abi.GetProperty("longSize").GetInt32() * 8;
        Provider = $"package:{profileName}";

        if (IsWindows)
            await ResolveWindowsToolchainAsync();
        else if (IsApple)
            await ResolveAppleToolchainAsync(options.Get("deploymentTarget"));
        else if (IsAndroid)
            ResolveAndroidToolchain(options);
        else if (IsBrowser && (Clang.Length == 0 || Sysroot.Length == 0))
            throw new InvalidOperationException("WebAssembly bindings require the Emscripten toolchain of the .NET wasm-tools workload " +
                "(or NativeBindingsClang and NativeBindingsSysroot).");
        if (Sysroot.Length != 0 && !Directory.Exists(Sysroot))
            throw new DirectoryNotFoundException($"The native sysroot does not exist: {Sysroot}");

        foreach (var root in selected.GetProperty("includeRoots").EnumerateArray())
        {
            var path = PackagePath(package, root.GetString()!);
            IncludeDirectories.Add(path);
            LibraryIncludeRoots.Add(path);
        }
        Defines.AddRange(selected.GetProperty("defines").EnumerateArray().Select(value => value.GetString()!));
        Undefines.AddRange(selected.GetProperty("undefines").EnumerateArray().Select(value => value.GetString()!));
        if (selected.TryGetProperty("wasm", out var wasm) && wasm.ValueKind == JsonValueKind.Object)
        {
            if (wasm.TryGetProperty("simd", out var simd) && simd.GetString() == "simd128")
                Arguments.Add("-msimd128");
            if (wasm.TryGetProperty("threads", out var threads) && threads.ValueKind == JsonValueKind.True)
                Arguments.Add("-pthread");
        }
        foreach (var header in selected.GetProperty("headers").EnumerateArray())
            await VerifyAsync(package, header);
        foreach (var library in selected.GetProperty("libraries").EnumerateArray().Where(library => library.GetProperty("role").GetString() == role))
        {
            var path = await VerifyAsync(package, library);
            Libraries.Add(new LibraryAsset(path, Backend != LinkBackend.Dynamic,
                library.TryGetProperty("runtimeIdentity", out var identity) ? identity.GetString() : null));
        }
    }

    private async Task LoadSystemAsync(Options options)
    {
        var hostArchitecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        if (IsWindows || IsBrowser || IsAndroid || Runtime.StartsWith("ios", StringComparison.Ordinal) || Architecture != hostArchitecture ||
            (OperatingSystem.IsLinux() != Runtime.StartsWith("linux", StringComparison.Ordinal)) ||
            (OperatingSystem.IsMacOS() != Runtime.StartsWith("osx-", StringComparison.Ordinal)))
            throw new InvalidOperationException($"Bindings for {Runtime} on a {RuntimeInformation.RuntimeIdentifier} build host require a native package " +
                "profile (NativeBindingsPackageDirectory). System libraries are used only for the build host's own runtime.");

        var modules = options.List("pkgConfigModules");
        var explicitIncludes = options.List("includeDirectories").Select(Path.GetFullPath).ToList();
        var explicitLibraries = options.List("libraryDirectories").Select(Path.GetFullPath).ToList();
        var names = options.List("libraries").ToList();
        if (modules.Length == 0 && (explicitIncludes.Count == 0 || explicitLibraries.Count == 0 || names.Count == 0))
            throw new InvalidOperationException("No native provider is configured. Set NativeBindingsPackageDirectory, NativeBindingsPkgConfigModules, " +
                "or NativeBindingsIncludeDirectory, NativeBindingsLibraryDirectory, and NativeBindingsLibraries.");

        var includes = new List<string>(explicitIncludes);
        var libraryDirectories = new List<string>(explicitLibraries);
        Provider = modules.Length != 0 ? "pkg-config:" + string.Join(' ', modules) : "system:" + string.Join(' ', names);
        if (modules.Length != 0)
        {
            var pkgConfig = PkgConfig(options);
            var available = await ProcessRunner.RunAsync(pkgConfig, new[] { "--print-errors", "--exists" }.Concat(modules), allowFailure: true);
            if (available.ExitCode != 0)
                throw new InvalidOperationException("Required native development packages are unavailable through pkg-config: " +
                    string.Join(", ", modules) + ". Install the matching development packages or configure PKG_CONFIG_PATH.\n" + available.Error);
            if (explicitIncludes.Count == 0)
            {
                foreach (var module in modules)
                    includes.Add((await ProcessRunner.RunAsync(pkgConfig, ["--variable=includedir", module])).Output.Trim());
                foreach (var flag in Tokens((await ProcessRunner.RunAsync(pkgConfig, new[] { "--cflags" }.Concat(modules))).Output))
                {
                    if (flag.StartsWith("-I", StringComparison.Ordinal))
                        includes.Add(flag[2..]);
                    else if (flag.StartsWith("-D", StringComparison.Ordinal))
                        Defines.Add(flag[2..]);
                    else if (flag.StartsWith("-U", StringComparison.Ordinal))
                        Undefines.Add(flag[2..]);
                    else
                        Arguments.Add(flag);
                }
            }
            if (explicitLibraries.Count == 0)
            {
                foreach (var module in modules)
                    libraryDirectories.Add((await ProcessRunner.RunAsync(pkgConfig, ["--variable=libdir", module])).Output.Trim());
                libraryDirectories.AddRange(Tokens((await ProcessRunner.RunAsync(pkgConfig, new[] { "--libs-only-L" }.Concat(modules))).Output)
                    .Where(flag => flag.StartsWith("-L", StringComparison.Ordinal)).Select(flag => flag[2..]));
            }
            if (names.Count == 0)
                names.AddRange(Tokens((await ProcessRunner.RunAsync(pkgConfig, new[] { "--libs-only-l" }.Concat(modules))).Output)
                    .Where(flag => flag.StartsWith("-l", StringComparison.Ordinal)).Select(flag => flag[2..]));
            foreach (var module in modules)
            {
                var path = await ProcessRunner.RunAsync(pkgConfig, ["--path", module], allowFailure: true);
                if (path.ExitCode == 0 && File.Exists(path.Output.Trim()))
                    DependencyFiles.Add(Path.GetFullPath(path.Output.Trim()));
            }
        }
        IncludeDirectories.AddRange(includes.Where(path => path.Length != 0).Select(Path.GetFullPath).Distinct(StringComparer.Ordinal));
        LibraryIncludeRoots.AddRange(IncludeDirectories);

        var extension = Backend != LinkBackend.Dynamic ? ".a" : IsApple ? ".dylib" : ".so";
        var missing = new List<string>();
        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            var path = libraryDirectories.Where(directory => directory.Length != 0)
                .Select(directory => Path.Combine(directory, "lib" + name + extension)).FirstOrDefault(File.Exists);
            if (path is null)
                missing.Add("lib" + name + extension);
            else
                Libraries.Add(new LibraryAsset(new FileInfo(path).ResolveLinkTarget(true)?.FullName ?? Path.GetFullPath(path),
                    Backend != LinkBackend.Dynamic, null, name));
        }
        if (missing.Count != 0)
            throw new FileNotFoundException($"Missing native {(Backend == LinkBackend.Dynamic ? "development" : "static")} libraries: " +
                string.Join(", ", missing) + " in " + string.Join(", ", libraryDirectories.Where(path => path.Length != 0).Distinct(StringComparer.Ordinal)) + "." +
                (Backend == LinkBackend.Dynamic ? "" : " Static linking requires the libraries' static archives."));
        if (Backend != LinkBackend.Dynamic)
        {
            // Keep the dependency order of the static link flags, linking the selected libraries' own archives in place.
            var archives = Libraries.ToDictionary(library => library.Name!, library => library.Path, StringComparer.Ordinal);
            var flags = modules.Length == 0 ? [] : Tokens((await ProcessRunner.RunAsync(PkgConfig(options), new[] { "--static", "--libs" }.Concat(modules))).Output);
            foreach (var flag in flags)
                LinkInputs.Add(flag.StartsWith("-l", StringComparison.Ordinal) && archives.Remove(flag[2..], out var archive) ? archive : flag);
            LinkInputs.InsertRange(0, archives.Values);
        }
        if (Clang.Length == 0)
            Clang = FindOnPath("clang") ?? "";
        if (SymbolTool.Length == 0 && Backend != LinkBackend.Dynamic)
            SymbolTool = IsApple ? "nm" : FindOnPath("nm") ?? FindOnPath("llvm-nm") ?? "nm";
        if (IsApple && Sysroot.Length == 0)
            Sysroot = (await ProcessRunner.RunAsync("xcrun", ["--show-sdk-path"])).Output.Trim();
    }

    private static IEnumerable<string> Tokens(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static string PkgConfig(Options options) =>
        options.Get("pkgConfig", Environment.GetEnvironmentVariable("PKG_CONFIG") is { Length: > 0 } configured ? configured : "pkg-config");

    private async Task ResolveWindowsToolchainAsync()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows bindings require a Windows build host with the MSVC build tools and a Windows SDK.");
        if (Clang.Length == 0)
        {
            var candidates = new List<string>();
            if (FindOnPath("clang.exe") is { } onPath)
                candidates.Add(onPath);
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LLVM", "bin", "clang.exe"));
            foreach (var installation in await VisualStudioInstallationsAsync())
                candidates.AddRange(VisualStudioLlvmDirectories(installation).Select(directory => Path.Combine(directory, "clang.exe")));
            Clang = candidates.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException(
                "Windows bindings require LLVM (clang.exe and libclang.dll). Install LLVM or the Visual Studio C++ Clang tools, or set NativeBindingsClang.");
        }
        if (SymbolTool.Length == 0 && Backend != LinkBackend.Dynamic)
            SymbolTool = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Clang))!, "llvm-nm.exe");
    }

    private static async Task<IEnumerable<string>> VisualStudioInstallationsAsync()
    {
        var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere))
            return [];
        var result = await ProcessRunner.RunAsync(vswhere, ["-products", "*", "-prerelease", "-sort", "-property", "installationPath"]);
        return result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    }

    private static IEnumerable<string> VisualStudioLlvmDirectories(string installation)
    {
        var host = RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "ARM64" : "x64";
        yield return Path.Combine(installation, "VC", "Tools", "Llvm", host, "bin");
        if (host != "x64")
            yield return Path.Combine(installation, "VC", "Tools", "Llvm", "x64", "bin");
    }

    private async Task ResolveAppleToolchainAsync(string deploymentTarget)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Apple bindings require a macOS build host with Xcode.");
        var sdk = Runtime.StartsWith("iossimulator-", StringComparison.Ordinal) ? "iphonesimulator" :
            Runtime.StartsWith("ios-", StringComparison.Ordinal) ? "iphoneos" :
            Runtime.StartsWith("osx-", StringComparison.Ordinal) ? "macosx" :
            throw new NotSupportedException($"Unsupported Apple runtime: {Runtime}");
        if (Clang.Length == 0)
            Clang = (await ProcessRunner.RunAsync("xcrun", ["--sdk", sdk, "--find", "clang"])).Output.Trim();
        if (SymbolTool.Length == 0)
            SymbolTool = (await ProcessRunner.RunAsync("xcrun", ["--sdk", sdk, "--find", "nm"])).Output.Trim();
        if (Sysroot.Length == 0)
            Sysroot = (await ProcessRunner.RunAsync("xcrun", ["--sdk", sdk, "--show-sdk-path"])).Output.Trim();
        var architecture = Architecture switch
        {
            "arm64" => "arm64",
            "x64" => "x86_64",
            _ => throw new NotSupportedException($"Unsupported Apple runtime: {Runtime}")
        };
        var version = deploymentTarget.Length != 0 ? deploymentTarget : sdk == "macosx" ? "11.0" : "15.0";
        if (!Version.TryParse(version, out _))
            throw new ArgumentException($"Invalid Apple deployment target: {version}");
        Triple = $"{architecture}-apple-{(sdk == "macosx" ? "macosx" : "ios")}{version}" + (sdk == "iphonesimulator" ? "-simulator" : "");
    }

    private void ResolveAndroidToolchain(Options options)
    {
        var ndk = new[] { options.Get("ndk"), Environment.GetEnvironmentVariable("ANDROID_NDK_ROOT"), Environment.GetEnvironmentVariable("ANDROID_NDK_HOME") }
            .FirstOrDefault(path => !string.IsNullOrEmpty(path)) ?? FindAndroidNdk(options.Get("androidSdk"));
        if (ndk.Length == 0)
            throw new InvalidOperationException("Could not find an Android NDK. Set NativeBindingsNdkDirectory, AndroidNdkDirectory, ANDROID_NDK_ROOT, " +
                "or provide an SDK containing an NDK through AndroidSdkDirectory, ANDROID_HOME, or ANDROID_SDK_ROOT.");
        var prebuilt = Path.Combine(ndk, "toolchains", "llvm", "prebuilt");
        if (!Directory.Exists(prebuilt))
            throw new DirectoryNotFoundException($"The Android NDK has no LLVM prebuilt toolchain: {prebuilt}");
        var host = OperatingSystem.IsLinux() ? "linux-" : OperatingSystem.IsMacOS() ? "darwin-" : "windows-";
        var toolchains = Directory.GetDirectories(prebuilt).Where(path => Path.GetFileName(path).StartsWith(host, StringComparison.Ordinal)).ToArray();
        if (toolchains.Length != 1)
            throw new InvalidOperationException($"Expected one {host}* toolchain under {prebuilt}; found {toolchains.Length}.");
        var extension = OperatingSystem.IsWindows() ? ".exe" : "";
        if (Clang.Length == 0)
            Clang = Path.Combine(toolchains[0], "bin", "clang" + extension);
        if (SymbolTool.Length == 0)
            SymbolTool = Path.Combine(toolchains[0], "bin", "llvm-nm" + extension);
        if (Sysroot.Length == 0)
            Sysroot = Path.Combine(toolchains[0], "sysroot");
    }

    private static string FindAndroidNdk(string configuredSdk)
    {
        foreach (var sdk in AndroidSdkDirectories(configuredSdk).Where(Directory.Exists).Distinct(StringComparer.Ordinal))
        {
            var sideBySide = Path.Combine(sdk, "ndk");
            var candidates = Directory.Exists(sideBySide) ? Directory.EnumerateDirectories(sideBySide) : [];
            var ndk = candidates.Append(Path.Combine(sdk, "ndk-bundle"))
                .Where(path => Directory.Exists(Path.Combine(path, "toolchains", "llvm", "prebuilt")))
                .Select(path => (Path: path, Version: AndroidNdkVersion(path)))
                .Where(candidate => candidate.Version is not null)
                .OrderByDescending(candidate => candidate.Version)
                .ThenBy(candidate => candidate.Path, StringComparer.Ordinal)
                .Select(candidate => candidate.Path)
                .FirstOrDefault();
            if (ndk is not null)
                return ndk;
        }
        return "";
    }

    private static Version? AndroidNdkVersion(string directory)
    {
        var properties = Path.Combine(directory, "source.properties");
        if (!File.Exists(properties))
            return null;
        foreach (var line in File.ReadLines(properties))
        {
            var parts = line.Split('=', 2);
            if (parts.Length == 2 && parts[0].Trim() == "Pkg.Revision" && Version.TryParse(parts[1].Trim(), out var version))
                return version;
        }
        return null;
    }

    private static IEnumerable<string> AndroidSdkDirectories(string configured)
    {
        foreach (var path in new[] { configured, Environment.GetEnvironmentVariable("ANDROID_HOME"), Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT") })
        {
            if (!string.IsNullOrEmpty(path))
                yield return path;
        }
        if (FindOnPath(OperatingSystem.IsWindows() ? "adb.exe" : "adb") is { } adb &&
            Directory.GetParent(new FileInfo(adb).ResolveLinkTarget(true)?.FullName ?? adb)?.Parent?.FullName is { } sdk)
            yield return sdk;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Android", "android-sdk");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Android", "android-sdk");
        }
        else
        {
            yield return Path.Combine(home, "Library", "Android", "sdk");
            yield return Path.Combine(home, "Android", "Sdk");
            if (OperatingSystem.IsLinux())
            {
                yield return "/usr/lib/android-sdk";
                yield return "/usr/share/android-sdk";
            }
        }
    }

    // Prefer the libclang of the selected toolchain, then the build host's default Clang installation.
    private async Task ResolveLibClangAsync(string configured)
    {
        if (configured.Length != 0)
            LibClangCandidates.Add(Path.GetFullPath(configured));
        if (Clang.Length != 0 && (Path.IsPathFullyQualified(Clang) ? Clang : FindOnPath(Clang)) is { } toolchain && File.Exists(toolchain))
            LibClangCandidates.AddRange(LibClangBeside(toolchain));
        if (OperatingSystem.IsMacOS())
        {
            var xcode = await ProcessRunner.RunAsync("xcrun", ["--find", "clang"], allowFailure: true);
            if (xcode.ExitCode == 0)
                LibClangCandidates.AddRange(LibClangBeside(xcode.Output.Trim()));
            LibClangCandidates.Add("/Library/Developer/CommandLineTools/usr/lib/libclang.dylib");
        }
        else if (OperatingSystem.IsWindows())
        {
            LibClangCandidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LLVM", "bin", "libclang.dll"));
            foreach (var installation in await VisualStudioInstallationsAsync())
                LibClangCandidates.AddRange(VisualStudioLlvmDirectories(installation).Select(directory => Path.Combine(directory, "libclang.dll")));
            LibClangCandidates.Add("libclang.dll");
        }
        else
        {
            if (FindOnPath("clang") is { } hostClang)
                LibClangCandidates.AddRange(LibClangBeside(hostClang));
            if (Directory.Exists("/usr/lib"))
                foreach (var directory in Directory.EnumerateDirectories("/usr/lib", "llvm-*").OrderByDescending(LlvmVersion))
                    LibClangCandidates.AddRange(SharedLibClang(Path.Combine(directory, "lib")));
            LibClangCandidates.AddRange(["libclang.so", "libclang.so.1"]);
        }
    }

    private static int LlvmVersion(string directory) =>
        int.TryParse(Path.GetFileName(directory)["llvm-".Length..], out var version) ? version : 0;

    private static IEnumerable<string> LibClangBeside(string compiler)
    {
        var resolved = new FileInfo(compiler).ResolveLinkTarget(true)?.FullName ?? Path.GetFullPath(compiler);
        var bin = Path.GetDirectoryName(resolved)!;
        if (OperatingSystem.IsWindows())
            return [Path.Combine(bin, "libclang.dll")];
        var root = Path.GetDirectoryName(bin)!;
        return OperatingSystem.IsMacOS()
            ? [Path.Combine(root, "lib", "libclang.dylib")]
            : SharedLibClang(Path.Combine(root, "lib")).Concat(SharedLibClang(Path.Combine(root, "lib64")));
    }

    private static IEnumerable<string> SharedLibClang(string directory)
    {
        if (!Directory.Exists(directory))
            return [];
        return Directory.EnumerateFiles(directory, "libclang*.so*")
            .Where(path => Path.GetFileName(path) is var name && (name == "libclang.so" || name.StartsWith("libclang.so.", StringComparison.Ordinal) ||
                (name.StartsWith("libclang-", StringComparison.Ordinal) && !name.StartsWith("libclang-cpp", StringComparison.Ordinal))))
            .OrderBy(path => Path.GetFileName(path) == "libclang.so" ? 0 : 1)
            .ThenByDescending(path => path, StringComparer.Ordinal);
    }

    internal static string? FindOnPath(string executable)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), executable);
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
            if (OperatingSystem.IsWindows() && !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(candidate + ".exe"))
                return Path.GetFullPath(candidate + ".exe");
        }
        return null;
    }

    private static string PackagePath(string package, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(package, relative));
        if (!path.StartsWith(package + Path.DirectorySeparatorChar, PathComparison))
            throw new InvalidOperationException($"Native package path escapes its root: {relative}");
        return path;
    }

    private async Task<string> VerifyAsync(string package, JsonElement asset)
    {
        var path = PackagePath(package, asset.GetProperty("path").GetString()!);
        await using (var stream = File.OpenRead(path))
        {
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream));
            if (!hash.Equals(asset.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Native package hash mismatch: {path}");
        }
        DependencyFiles.Add(path);
        return path;
    }
}
