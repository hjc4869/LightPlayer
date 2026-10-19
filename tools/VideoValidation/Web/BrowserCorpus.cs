using System.Security.Cryptography;
using System.Text.Json;

namespace LightStudio.VideoValidation.Web;

internal static class BrowserCorpus
{
    public static async Task PrepareAsync(
        string outputDirectory,
        Uri corpusBaseUri,
        IReadOnlyList<ValidationCase> cases,
        Action<string> reportProgress,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient { BaseAddress = corpusBaseUri };
        await using Stream manifestStream = await client.GetStreamAsync("assets-manifest.json", cancellationToken);
        ValidationAssetManifest manifest =
            await JsonSerializer.DeserializeAsync(
                manifestStream,
                ValidationJsonContext.Default.ValidationAssetManifest,
                cancellationToken)
            ?? throw new InvalidDataException("The browser corpus manifest is empty.");
        if (manifest.Version != ValidationAssetManifest.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Corpus manifest v{manifest.Version} is incompatible with v{ValidationAssetManifest.CurrentVersion}; regenerate it.");
        }

        var selectedAssetIds = cases.Select(item => item.AssetId).ToHashSet(StringComparer.Ordinal);
        GeneratedValidationAsset[] selectedAssets = manifest.Assets
            .Where(asset => selectedAssetIds.Contains(asset.Id) && asset.Generated)
            .ToArray();
        var rewrittenAssets = new List<GeneratedValidationAsset>(manifest.Assets.Count);
        int loaded = 0;

        foreach (GeneratedValidationAsset asset in manifest.Assets)
        {
            if (!selectedAssetIds.Contains(asset.Id) || !asset.Generated)
            {
                rewrittenAssets.Add(asset);
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            reportProgress($"Loading corpus {++loaded}/{selectedAssets.Length}: {asset.Id}");

            string sourcePath = RequiredLocalPath(outputDirectory, "assets", asset.FilePath);
            string referencePath = RequiredLocalPath(outputDirectory, "references", asset.ReferencePath);
            string linearReferencePath = LocalPath(outputDirectory, "references", asset.LinearReferencePath) ?? string.Empty;
            string signalReferencePath = LocalPath(outputDirectory, "references", asset.SignalReferencePath) ?? string.Empty;

            await DownloadAsync(
                client,
                RequiredRelativeUrl("assets", asset.FilePath),
                sourcePath,
                asset.ByteLength,
                asset.Sha256,
                cancellationToken);
            await DownloadAsync(client, RelativeUrl("references", asset.ReferencePath), referencePath, cancellationToken);
            await DownloadAsync(client, RelativeUrl("references", asset.LinearReferencePath), linearReferencePath, cancellationToken);
            await DownloadAsync(client, RelativeUrl("references", asset.SignalReferencePath), signalReferencePath, cancellationToken);

            rewrittenAssets.Add(asset with
            {
                FilePath = sourcePath,
                ReferencePath = referencePath,
                LinearReferencePath = linearReferencePath,
                SignalReferencePath = signalReferencePath,
                LogPath = string.Empty,
            });
        }

        Directory.CreateDirectory(outputDirectory);
        var localManifest = manifest with { Assets = rewrittenAssets };
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "assets-manifest.json"),
            JsonSerializer.Serialize(localManifest, ValidationJsonContext.Default.ValidationAssetManifest),
            cancellationToken);
    }

    private static Task DownloadAsync(
        HttpClient client,
        string? relativeUrl,
        string? destination,
        CancellationToken cancellationToken) =>
        string.IsNullOrEmpty(relativeUrl) || string.IsNullOrEmpty(destination)
            ? Task.CompletedTask
            : DownloadAsync(client, relativeUrl, destination, null, null, cancellationToken);

    private static async Task DownloadAsync(
        HttpClient client,
        string relativeUrl,
        string destination,
        long? expectedLength,
        string? expectedSha256,
        CancellationToken cancellationToken)
    {
        byte[] content = await client.GetByteArrayAsync(relativeUrl, cancellationToken);
        if (expectedLength > 0 && content.LongLength != expectedLength)
        {
            throw new InvalidDataException(
                $"Corpus asset '{relativeUrl}' is {content.LongLength} bytes; expected {expectedLength}.");
        }

        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            string actualSha256 = Convert.ToHexStringLower(SHA256.HashData(content));
            if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Corpus asset '{relativeUrl}' failed SHA-256 verification.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllBytesAsync(destination, content, cancellationToken);
    }

    private static string? LocalPath(string outputDirectory, string directory, string sourcePath) =>
        string.IsNullOrWhiteSpace(sourcePath)
            ? null
            : Path.Combine(outputDirectory, directory, FileName(sourcePath));

    private static string RequiredLocalPath(string outputDirectory, string directory, string sourcePath) =>
        LocalPath(outputDirectory, directory, sourcePath)
        ?? throw new InvalidDataException($"Corpus asset '{sourcePath}' has no file name.");

    private static string? RelativeUrl(string directory, string sourcePath) =>
        string.IsNullOrWhiteSpace(sourcePath)
            ? null
            : $"{directory}/{Uri.EscapeDataString(FileName(sourcePath))}";

    private static string RequiredRelativeUrl(string directory, string sourcePath) =>
        RelativeUrl(directory, sourcePath)
        ?? throw new InvalidDataException($"Corpus asset '{sourcePath}' has no URL path.");

    private static string FileName(string path) =>
        Path.GetFileName(path.Replace('\\', Path.DirectorySeparatorChar));
}