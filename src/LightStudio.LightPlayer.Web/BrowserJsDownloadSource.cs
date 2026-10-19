using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.Lyrics;
using LightStudio.MediaLibraryCore.Lyrics.RuntimeApi;

namespace LightStudio.LightPlayer.Web;

[SupportedOSPlatform("browser")]
internal sealed class BrowserJsDownloadSource : IJsDownloadSource
{
    private readonly int sourceId;

    public BrowserJsDownloadSource(string content, string name)
    {
        sourceId = Invoke(() => BrowserLyricInterop.Create(content));
        Name = name;
    }

    public string Name { get; set; }

    public IReadOnlyList<LyricSourceInfoItem> GetSourceInfo() =>
        JsDownloadSourceJson.DeserializeSourceInfo(Invoke(() => BrowserLyricInterop.GetSourceInfo(sourceId)));

    public async Task<ExternalLrcInfo[]> LookupLrcAsync(
        string title,
        string artist,
        CancellationToken cancellationToken = default)
    {
        var json = await RunAsync(
            requestId => BrowserLyricInterop.LookupAsync(requestId, title, artist), cancellationToken);
        return JsDownloadSourceJson.DeserializeLookup(json, Name);
    }

    public async Task<LyricDownload> DownloadLrcAsync(
        ExternalLrcInfo result,
        CancellationToken cancellationToken = default)
    {
        var candidateJson = JsDownloadSourceJson.SerializeCandidate(result);
        var json = await RunAsync(
            requestId => BrowserLyricInterop.DownloadAsync(requestId, candidateJson), cancellationToken);
        return JsDownloadSourceJson.DeserializeDownload(json);
    }

    private Task<string> RunAsync(Func<int, Task<string>> operation, CancellationToken cancellationToken) =>
        BrowserMainThread.InvokeAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requestId = BrowserLyricInterop.BeginRequest(sourceId);
            await using var registration = cancellationToken.UnsafeRegister(
                static state => _ = CancelAsync((int)state!), requestId);
            try
            {
                var json = await operation(requestId);
                cancellationToken.ThrowIfCancellationRequested();
                return json;
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            finally
            {
                BrowserLyricInterop.EndRequest(requestId);
            }
        });

    private static async Task CancelAsync(int requestId)
    {
        try
        {
            await BrowserMainThread.InvokeAsync(() =>
            {
                BrowserLyricInterop.Cancel(requestId);
                return Task.CompletedTask;
            });
        }
        catch (Exception exception)
        {
            AppLog.Warning("Lyrics.Browser", "Could not cancel the lyric script request.", exception);
        }
    }

    private static T Invoke<T>(Func<T> operation) =>
        BrowserMainThread.InvokeAsync(() => Task.FromResult(operation())).GetAwaiter().GetResult();

    public void Dispose() => Invoke(() =>
    {
        BrowserLyricInterop.Dispose(sourceId);
        return true;
    });
}

[SupportedOSPlatform("browser")]
internal static partial class BrowserLyricInterop
{
    [JSImport("create", "LyricSources")]
    internal static partial int Create(string content);

    [JSImport("getSourceInfo", "LyricSources")]
    internal static partial string GetSourceInfo(int sourceId);

    [JSImport("beginRequest", "LyricSources")]
    internal static partial int BeginRequest(int sourceId);

    [JSImport("lookup", "LyricSources")]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    internal static partial Task<string> LookupAsync(int requestId, string title, string artist);

    [JSImport("download", "LyricSources")]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    internal static partial Task<string> DownloadAsync(int requestId, string candidateJson);

    [JSImport("cancel", "LyricSources")]
    internal static partial void Cancel(int requestId);

    [JSImport("endRequest", "LyricSources")]
    internal static partial void EndRequest(int requestId);

    [JSImport("dispose", "LyricSources")]
    internal static partial void Dispose(int sourceId);
}