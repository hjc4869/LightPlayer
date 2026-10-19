using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LightStudio.VideoValidation.Web;

internal sealed record BrowserValidationConfiguration
{
    public bool AutoRun { get; init; }

    public string CorpusBaseUri { get; init; } = string.Empty;

    public IReadOnlyList<string> CaseIds { get; init; } = [];
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(BrowserValidationConfiguration))]
internal sealed partial class BrowserValidationJsonContext : JsonSerializerContext;

[SupportedOSPlatform("browser")]
internal static partial class BrowserValidationInterop
{
    public static BrowserValidationConfiguration GetConfiguration() =>
        JsonSerializer.Deserialize(
            GetConfigurationJson(),
            BrowserValidationJsonContext.Default.BrowserValidationConfiguration)
        ?? throw new InvalidOperationException("The browser did not provide validation configuration.");

    public static IReadOnlyDictionary<string, string> GetInventory()
    {
        using JsonDocument document = JsonDocument.Parse(GetInventoryJson());
        return document.RootElement
            .EnumerateObject()
            .ToDictionary(
                property => property.Name,
                property => property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.GetRawText(),
                StringComparer.Ordinal);
    }

    [JSImport("getConfiguration", "ValidationBridge")]
    private static partial string GetConfigurationJson();

    [JSImport("getInventory", "ValidationBridge")]
    private static partial string GetInventoryJson();

    [JSImport("setStatus", "ValidationBridge")]
    internal static partial void SetStatus(string status, string message);

    [JSImport("complete", "ValidationBridge")]
    internal static partial void Complete(
        string reportJson,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> bundle,
        string fileName);

    [JSImport("fail", "ValidationBridge")]
    internal static partial void Fail(string message);

    [JSImport("download", "ValidationBridge")]
    internal static partial void Download();
}