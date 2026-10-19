using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Shaders;
using LightStudio.Logging;

namespace LightMediaRenderer.Avalonia.Shaders;

/// <summary>The on-disk contract for one distributable video pre-scale stage.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VideoShaderPackageManifest
{
    public int SchemaVersion { get; init; }

    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public string Author { get; init; } = string.Empty;

    public string License { get; init; } = string.Empty;

    public string LicenseFile { get; init; } = string.Empty;

    public string Stage { get; init; } = "stage.json";
}

/// <summary>An installed shader package together with this device's automatic-selection settings.</summary>
public sealed record VideoShaderPackage
{
    private readonly Lazy<FileVideoScaleStage?> stage;

    internal VideoShaderPackage(
        VideoShaderPackageManifest manifest,
        string directoryPath,
        string stagePath,
        string stageName,
        int passCount,
        bool isEnabled,
        int maximumSourceResolution,
        FileVideoScaleStage? preloadedStage = null)
    {
        Manifest = manifest;
        DirectoryPath = directoryPath;
        StageName = stageName;
        PassCount = passCount;
        IsEnabled = isEnabled;
        MaximumSourceResolution = maximumSourceResolution;
        stage = new Lazy<FileVideoScaleStage?>(() =>
        {
            if (preloadedStage is not null)
                return preloadedStage;
            if (FileVideoScaleStage.TryLoad(stagePath, out FileVideoScaleStage? loaded, out string error))
                return loaded;
            AppLog.Warning(
                "Video.ShaderPackage",
                $"Could not compile shader package {manifest.Id}: {error}");
            return null;
        });
        if (preloadedStage is not null)
            _ = stage.Value;
    }

    public VideoShaderPackageManifest Manifest { get; }

    public string Id => Manifest.Id;

    public string DirectoryPath { get; }

    public bool IsEnabled { get; internal set; }

    /// <summary>
    /// Largest source short edge eligible for automatic selection, in pixels; zero is unlimited.
    /// </summary>
    public int MaximumSourceResolution { get; internal set; }

    public string StageName { get; }

    public int PassCount { get; }

    internal FileVideoScaleStage? ResolveStage() => stage.Value;

    internal FileVideoScaleStage? LoadedStage => stage.IsValueCreated ? stage.Value : null;
}

/// <summary>
/// Device-local registry for imported shader-package folders. Package files live beside a compact
/// catalog that carries only mutable order and automatic-selection preferences.
/// </summary>
public sealed class VideoShaderPackageCatalog : IDisposable
{
    public const string DisabledOverride = "$off";
    public const int CurrentPackageSchemaVersion = 1;
    public const int MaximumSourceResolution = 16384;

    private const int CurrentCatalogVersion = 1;
    private const int MaximumPackageFiles = 512;
    private const int MaximumPackageDirectories = 128;
    private const long MaximumPackageBytes = 64L * 1024 * 1024;
    private const string PackageFileName = "package.json";
    private const string CatalogFileName = "catalog.json";

    private static readonly Regex PackageIdPattern = new(
        "^[a-z0-9][a-z0-9._-]{1,126}[a-z0-9]$",
        RegexOptions.CultureInvariant);
    private static readonly Regex VersionPattern = new(
        "^[0-9]+\\.[0-9]+\\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?$",
        RegexOptions.CultureInvariant);

    private readonly object sync = new();
    private readonly string rootDirectory;
    private readonly string packagesDirectory;
    private readonly string catalogPath;
    private readonly List<VideoShaderPackage> packages = [];
    private readonly List<FileVideoScaleStage> retiredStages = [];
    private readonly IVideoScaleStage automaticSelector;
    private readonly Dictionary<string, IVideoScaleStage> manualSelectors = new(StringComparer.Ordinal);
    private bool disposed;

    public VideoShaderPackageCatalog(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        this.rootDirectory = Path.GetFullPath(rootDirectory);
        packagesDirectory = Path.Combine(this.rootDirectory, "packages");
        catalogPath = Path.Combine(this.rootDirectory, CatalogFileName);
        Directory.CreateDirectory(packagesDirectory);
        Load();
        automaticSelector = new PackageSelector(this, packageId: null);
    }

    public event EventHandler? Changed;

    public IReadOnlyList<VideoShaderPackage> Packages
    {
        get
        {
            lock (sync)
                return packages.ToArray();
        }
    }

    /// <summary>Validates package metadata, containment, license, and stage compilation in place.</summary>
    public static bool TryValidatePackage(
        string packageDirectory,
        out VideoShaderPackageManifest? manifest,
        out string error)
    {
        if (!TryValidatePackageCore(packageDirectory, out manifest, out FileVideoScaleStage? stage, out error))
            return false;
        stage!.Dispose();
        return true;
    }

    private static bool TryValidatePackageCore(
        string packageDirectory,
        out VideoShaderPackageManifest? manifest,
        out FileVideoScaleStage? stage,
        out string error)
    {
        manifest = null;
        stage = null;
        try
        {
            string directory = Path.GetFullPath(packageDirectory);
            manifest = ReadManifest(directory);
            string stagePath = ContainedFile(directory, manifest.Stage, "stage");
            _ = ContainedFile(directory, manifest.LicenseFile, "license");
            if (!FileVideoScaleStage.TryLoad(stagePath, out stage, out error))
                return false;
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            AppLog.Warning("Video.ShaderPackage", $"Could not validate shader package {packageDirectory}.", exception);
            error = exception.GetBaseException().Message;
            return false;
        }
    }

    /// <summary>Returns automatic priority, a forced package, or null for explicit off.</summary>
    public IVideoScaleStage? GetStage(string? packageOverride)
    {
        if (string.Equals(packageOverride, DisabledOverride, StringComparison.Ordinal))
            return null;
        if (string.IsNullOrEmpty(packageOverride))
        {
            lock (sync)
                return packages.Count == 0 ? null : automaticSelector;
        }

        lock (sync)
        {
            if (!manualSelectors.TryGetValue(packageOverride, out IVideoScaleStage? selector))
                manualSelectors[packageOverride] = selector = new PackageSelector(this, packageOverride);
            return selector;
        }
    }

    /// <summary>Imports one folder and returns the installed package after validation.</summary>
    public Task<VideoShaderPackage> ImportAsync(
        string sourceDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        return Task.Run(() => Import(sourceDirectory, cancellationToken), cancellationToken);
    }

    /// <summary>Streams an opaque picked folder into app storage before validating it.</summary>
    public async Task<VideoShaderPackage> ImportAsync(
        IStorageFolder sourceFolder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceFolder);
        string staging = Path.Combine(rootDirectory, $".import-{Guid.NewGuid():N}");
        try
        {
            var budget = new ImportBudget();
            await CopyPackageAsync(sourceFolder, staging, budget, cancellationToken).ConfigureAwait(false);
            return InstallStaged(staging);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    public bool Move(string id, int newIndex)
    {
        lock (sync)
        {
            int currentIndex = packages.FindIndex(package => package.Id == id);
            if (currentIndex < 0 || newIndex < 0 || newIndex >= packages.Count || currentIndex == newIndex)
                return false;

            VideoShaderPackage package = packages[currentIndex];
            packages.RemoveAt(currentIndex);
            packages.Insert(newIndex, package);
            try
            {
                Save();
            }
            catch
            {
                packages.RemoveAt(newIndex);
                packages.Insert(currentIndex, package);
                throw;
            }
        }

        NotifyChanged();
        return true;
    }

    public bool SetEnabled(string id, bool enabled)
    {
        lock (sync)
        {
            VideoShaderPackage? package = packages.FirstOrDefault(candidate => candidate.Id == id);
            if (package is null || package.IsEnabled == enabled)
                return false;

            bool previous = package.IsEnabled;
            package.IsEnabled = enabled;
            try
            {
                Save();
            }
            catch
            {
                package.IsEnabled = previous;
                throw;
            }
        }

        NotifyChanged();
        return true;
    }

    public bool SetMaximumSourceResolution(string id, int value)
    {
        value = Math.Clamp(value, 0, MaximumSourceResolution);
        lock (sync)
        {
            VideoShaderPackage? package = packages.FirstOrDefault(candidate => candidate.Id == id);
            if (package is null || package.MaximumSourceResolution == value)
                return false;

            int previous = package.MaximumSourceResolution;
            package.MaximumSourceResolution = value;
            try
            {
                Save();
            }
            catch
            {
                package.MaximumSourceResolution = previous;
                throw;
            }
        }

        NotifyChanged();
        return true;
    }

    public bool Remove(string id)
    {
        string? directory = null;
        lock (sync)
        {
            int index = packages.FindIndex(package => package.Id == id);
            if (index < 0)
                return false;

            VideoShaderPackage package = packages[index];
            packages.RemoveAt(index);
            manualSelectors.Remove(id);
            directory = package.DirectoryPath;
            try
            {
                Save();
            }
            catch
            {
                packages.Insert(index, package);
                throw;
            }
            if (package.LoadedStage is { } loadedStage)
                retiredStages.Add(loadedStage);
        }

        TryDeleteDirectory(directory);
        NotifyChanged();
        return true;
    }

    private VideoShaderPackage Import(string sourceDirectory, CancellationToken cancellationToken)
    {
        string source = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"Shader package folder does not exist: {source}");
        if (ContainsPath(source, rootDirectory))
            throw new InvalidDataException("The selected folder contains Light Player's shader package storage.");

        string staging = Path.Combine(rootDirectory, $".import-{Guid.NewGuid():N}");
        try
        {
            CopyPackage(source, staging, cancellationToken);
            return InstallStaged(staging);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    private VideoShaderPackage InstallStaged(string staging)
    {
        if (!TryValidatePackageCore(
            staging,
            out VideoShaderPackageManifest? manifest,
            out FileVideoScaleStage? validatedStage,
            out string error))
        {
            throw new InvalidDataException($"The shader package is invalid: {error}");
        }

        FileVideoScaleStage? unownedStage = validatedStage;
        string target = Path.Combine(packagesDirectory, StorageName(manifest!.Id));
        string backup = $"{target}.old-{Guid.NewGuid():N}";
        bool hadTarget = Directory.Exists(target);
        try
        {
            if (hadTarget)
                Directory.Move(target, backup);
            Directory.Move(staging, target);

            VideoShaderPackage installed = LoadPackage(
                target,
                manifest.Id,
                isEnabled: true,
                maximumSourceResolution: 0,
                preloadedStage: unownedStage);
            unownedStage = null;
            lock (sync)
            {
                int existingIndex = packages.FindIndex(package => package.Id == manifest.Id);
                manualSelectors.Remove(manifest.Id);
                VideoShaderPackage? existing = existingIndex >= 0 ? packages[existingIndex] : null;
                if (existingIndex >= 0)
                {
                    existing = packages[existingIndex];
                    installed.IsEnabled = existing.IsEnabled;
                    installed.MaximumSourceResolution = existing.MaximumSourceResolution;
                    packages[existingIndex] = installed;
                }
                else
                {
                    packages.Add(installed);
                }
                try
                {
                    Save();
                }
                catch
                {
                    if (existingIndex >= 0)
                        packages[existingIndex] = existing!;
                    else
                        packages.Remove(installed);
                    if (installed.LoadedStage is { } installedStage)
                        retiredStages.Add(installedStage);
                    throw;
                }
                if (existing?.LoadedStage is { } existingStage)
                    retiredStages.Add(existingStage);
            }

            TryDeleteDirectory(backup);
            NotifyChanged();
            return installed;
        }
        catch
        {
            unownedStage?.Dispose();
            TryDeleteDirectory(target);
            if (hadTarget && Directory.Exists(backup))
                Directory.Move(backup, target);
            throw;
        }
    }

    private void Load()
    {
        PersistedVideoShaderCatalog state = ReadCatalog();
        if (state.Version != CurrentCatalogVersion)
        {
            AppLog.Warning("Video.ShaderPackage", $"Ignoring unsupported shader package catalog version {state.Version}.");
            return;
        }
        var loadedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (PersistedVideoShaderPackage entry in state.Packages)
        {
            try
            {
                if (!loadedIds.Add(entry.Id))
                    throw new InvalidDataException("The package id appears more than once in the catalog.");
                string directory = Path.Combine(packagesDirectory, StorageName(entry.Id));
                packages.Add(LoadPackage(
                    directory,
                    entry.Id,
                    entry.IsEnabled,
                    Math.Clamp(entry.MaximumSourceResolution, 0, MaximumSourceResolution)));
            }
            catch (Exception exception)
            {
                AppLog.Warning(
                    "Video.ShaderPackage",
                    $"Could not load installed shader package {entry.Id}.",
                    exception);
            }
        }
    }

    private static VideoShaderPackage LoadPackage(
        string directory,
        string expectedId,
        bool isEnabled,
        int maximumSourceResolution,
        FileVideoScaleStage? preloadedStage = null)
    {
        VideoShaderPackageManifest manifest = ReadManifest(directory);
        if (!string.Equals(manifest.Id, expectedId, StringComparison.Ordinal))
            throw new InvalidDataException("The installed package identity does not match the catalog.");

        string stagePath = ContainedFile(directory, manifest.Stage, "stage");
        _ = ContainedFile(directory, manifest.LicenseFile, "license");
        (string stageName, int passCount) = ReadStageSummary(stagePath);
        return new VideoShaderPackage(
            manifest,
            directory,
            stagePath,
            stageName,
            passCount,
            isEnabled,
            maximumSourceResolution,
            preloadedStage);
    }

    private static (string Name, int PassCount) ReadStageSummary(string stagePath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(stagePath));
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("name", out JsonElement nameElement) ||
            nameElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(nameElement.GetString()) ||
            !root.TryGetProperty("passes", out JsonElement passesElement) ||
            passesElement.ValueKind != JsonValueKind.Array ||
            passesElement.GetArrayLength() is < 1 or > 256)
        {
            throw new InvalidDataException("The package stage metadata is invalid.");
        }
        return (nameElement.GetString()!, passesElement.GetArrayLength());
    }

    private static VideoShaderPackageManifest ReadManifest(string directory)
    {
        string path = Path.Combine(directory, PackageFileName);
        if (!File.Exists(path))
            throw new InvalidDataException($"A shader package must contain {PackageFileName} at its root.");

        VideoShaderPackageManifest manifest = JsonSerializer.Deserialize(
            File.ReadAllText(path),
            VideoShaderPackageJsonContext.Default.VideoShaderPackageManifest)
            ?? throw new InvalidDataException("The shader package manifest is empty.");
        ValidateManifest(manifest);
        return manifest;
    }

    private static void ValidateManifest(VideoShaderPackageManifest manifest)
    {
        if (manifest.SchemaVersion != CurrentPackageSchemaVersion)
            throw new InvalidDataException($"Shader package schema {manifest.SchemaVersion} is not supported.");
        if (manifest.Id is null || !PackageIdPattern.IsMatch(manifest.Id))
            throw new InvalidDataException("Package id must be 3-128 lowercase letters, digits, dots, underscores, or hyphens.");
        if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 128)
            throw new InvalidDataException("Package name must contain 1-128 characters.");
        if (manifest.Description is null || manifest.Description.Length > 1024)
            throw new InvalidDataException("Package description cannot exceed 1024 characters.");
        if (manifest.Version is null || !VersionPattern.IsMatch(manifest.Version))
            throw new InvalidDataException("Package version must use semantic version form, such as 1.0.0.");
        if (string.IsNullOrWhiteSpace(manifest.Author) || manifest.Author.Length > 128)
            throw new InvalidDataException("Package author must contain 1-128 characters.");
        if (string.IsNullOrWhiteSpace(manifest.License) || manifest.License.Length > 64)
            throw new InvalidDataException("Package license must contain 1-64 characters.");
        if (string.IsNullOrWhiteSpace(manifest.LicenseFile))
            throw new InvalidDataException("Package licenseFile is required.");
        if (string.IsNullOrWhiteSpace(manifest.Stage))
            throw new InvalidDataException("Package stage is required.");
    }

    private static string ContainedFile(string directory, string relativePath, string kind)
    {
        string root = Path.GetFullPath(directory);
        string path = Path.GetFullPath(Path.Combine(root, relativePath));
        string relative = Path.GetRelativePath(root, path);
        if (relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            throw new InvalidDataException($"Package {kind} path escapes the package folder.");
        }
        if (!File.Exists(path))
            throw new InvalidDataException($"Package {kind} file does not exist: {relativePath}");
        return path;
    }

    private static void CopyPackage(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        int fileCount = 0;
        int directoryCount = 1;
        long totalBytes = 0;
        var pending = new Stack<(string Source, string Destination)>();
        pending.Push((source, destination));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string currentSource, string currentDestination) = pending.Pop();
            foreach (string directory in Directory.EnumerateDirectories(currentSource))
            {
                RejectLink(directory);
                directoryCount++;
                if (directoryCount > MaximumPackageDirectories)
                    throw new InvalidDataException("Shader package exceeds the directory-count limit.");
                string childDestination = Path.Combine(currentDestination, Path.GetFileName(directory));
                Directory.CreateDirectory(childDestination);
                pending.Push((directory, childDestination));
            }
            foreach (string file in Directory.EnumerateFiles(currentSource))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RejectLink(file);
                FileInfo info = new(file);
                fileCount++;
                totalBytes = checked(totalBytes + info.Length);
                if (fileCount > MaximumPackageFiles || totalBytes > MaximumPackageBytes)
                    throw new InvalidDataException("Shader package exceeds the import size limit.");
                File.Copy(file, Path.Combine(currentDestination, info.Name), overwrite: false);
            }
        }
    }

    private static async Task CopyPackageAsync(
        IStorageFolder source,
        string destination,
        ImportBudget budget,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        budget.Directories++;
        if (budget.Directories > MaximumPackageDirectories)
            throw new InvalidDataException("Shader package exceeds the directory-count limit.");

        await foreach (IStorageItem item in source.GetItemsAsync())
        {
            using (item)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = item.Name;
                if (string.IsNullOrWhiteSpace(name) ||
                    name is "." or ".." ||
                    name.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
                    name.IndexOf(Path.AltDirectorySeparatorChar) >= 0 ||
                    !string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Shader package contains an invalid item name.");
                }

                string target = Path.Combine(destination, name);
                if (item is IStorageFolder folder)
                {
                    await CopyPackageAsync(folder, target, budget, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (item is not IStorageFile file)
                    throw new InvalidDataException($"Shader package contains unsupported item {name}.");

                budget.Files++;
                if (budget.Files > MaximumPackageFiles)
                    throw new InvalidDataException("Shader package exceeds the file-count limit.");
                await using Stream input = await file.OpenReadAsync().ConfigureAwait(false);
                await using FileStream output = File.Create(target);
                byte[] buffer = new byte[81920];
                while (true)
                {
                    int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    budget.Bytes = checked(budget.Bytes + read);
                    if (budget.Bytes > MaximumPackageBytes)
                        throw new InvalidDataException("Shader package exceeds the import size limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Shader packages cannot contain symbolic links or reparse points.");
    }

    private sealed class ImportBudget
    {
        public int Files { get; set; }

        public int Directories { get; set; }

        public long Bytes { get; set; }
    }

    private PersistedVideoShaderCatalog ReadCatalog()
    {
        try
        {
            if (File.Exists(catalogPath))
            {
                PersistedVideoShaderCatalog loaded = JsonSerializer.Deserialize(
                    File.ReadAllText(catalogPath),
                    VideoShaderPackageJsonContext.Default.PersistedVideoShaderCatalog)
                    ?? new PersistedVideoShaderCatalog();
                loaded.Packages ??= [];
                return loaded;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warning("Video.ShaderPackage", "Could not read the shader package catalog.", exception);
        }
        return new PersistedVideoShaderCatalog();
    }

    private void Save()
    {
        Directory.CreateDirectory(rootDirectory);
        var state = new PersistedVideoShaderCatalog
        {
            Version = CurrentCatalogVersion,
            Packages = packages.Select(package => new PersistedVideoShaderPackage
            {
                Id = package.Id,
                IsEnabled = package.IsEnabled,
                MaximumSourceResolution = package.MaximumSourceResolution,
            }).ToList(),
        };
        string json = JsonSerializer.Serialize(
            state,
            VideoShaderPackageJsonContext.Default.PersistedVideoShaderCatalog);
        string temporary = $"{catalogPath}.tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, catalogPath, overwrite: true);
    }

    private static string StorageName(string id)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(id));
        return Convert.ToHexStringLower(hash);
    }

    private static bool ContainsPath(string directory, string candidate)
    {
        string relative = Path.GetRelativePath(directory, candidate);
        return relative != ".." &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative);
    }

    private static void TryDeleteDirectory(string? path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning("Video.ShaderPackage", $"Could not remove package folder {path}.", exception);
        }
    }

    private void NotifyChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            AppLog.Error("Video.ShaderPackage", "A shader package catalog listener failed.", exception);
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
            foreach (VideoShaderPackage package in packages)
                package.LoadedStage?.Dispose();
            foreach (FileVideoScaleStage stage in retiredStages)
                stage.Dispose();
            packages.Clear();
            retiredStages.Clear();
            manualSelectors.Clear();
        }
    }

    private IVideoScaleStage? ResolvePackage(string? packageId, in VideoScaleStageContext context)
    {
        lock (sync)
        {
            if (packageId is not null)
            {
                VideoShaderPackage? package = packages.FirstOrDefault(candidate => candidate.Id == packageId);
                FileVideoScaleStage? stage = package?.ResolveStage();
                return stage?.Supports(context) == true ? stage : null;
            }

            int sourceResolution = Math.Min(context.Scaling.SourceWidth, context.Scaling.SourceHeight);
            foreach (VideoShaderPackage package in packages)
            {
                if (!package.IsEnabled ||
                    (package.MaximumSourceResolution > 0 && sourceResolution > package.MaximumSourceResolution))
                {
                    continue;
                }
                FileVideoScaleStage? stage = package.ResolveStage();
                if (stage?.ShouldRun(context) == true)
                    return stage;
            }
            return null;
        }
    }

    private sealed class PackageSelector(VideoShaderPackageCatalog catalog, string? packageId) : IVideoScaleStage
    {
        public string Name => packageId ?? "Automatic";

        public int PassCount => 0;

        public bool ShouldRun(in VideoScaleStageContext context) =>
            catalog.ResolvePackage(packageId, context) is not null;

        public IVideoScaleStage? Resolve(in VideoScaleStageContext context) =>
            catalog.ResolvePackage(packageId, context);

        public SkiaSharp.SKImage? Render(
            SkiaSharp.SKImage source,
            SkiaSharp.GRContext? grContext,
            in VideoScaleStageContext context,
            out string declined) =>
            throw new InvalidOperationException("A package selector must resolve before rendering.");
    }
}

internal sealed class PersistedVideoShaderCatalog
{
    public int Version { get; set; } = 1;

    public List<PersistedVideoShaderPackage> Packages { get; set; } = [];
}

internal sealed class PersistedVideoShaderPackage
{
    public string Id { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;

    public int MaximumSourceResolution { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(VideoShaderPackageManifest))]
[JsonSerializable(typeof(PersistedVideoShaderCatalog))]
internal sealed partial class VideoShaderPackageJsonContext : JsonSerializerContext;