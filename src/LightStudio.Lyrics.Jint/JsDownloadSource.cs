using Jint;
using LightStudio.MediaLibraryCore.Lyrics.RuntimeApi;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LightStudio.MediaLibraryCore.Lyrics;

public class JsDownloadSource : IJsDownloadSource
{
    private const string ContractAdapter = """
        function __getSourceInfoJson() {
            return JSON.stringify(typeof getSourceInfo === "function" ? getSourceInfo() : []);
        }

        async function __lookupLrcJsonAsync(title, artist, signal) {
            return JSON.stringify(await lookupLrcAsync(title, artist, signal));
        }

        async function __downloadLrcJsonAsync(lrcInfoJson, signal) {
            return JSON.stringify(await downloadLrcAsync(JSON.parse(lrcInfoJson), signal));
        }
        """;

    private readonly Engine host;
    private readonly SemaphoreSlim engineLock = new(1, 1);

    public string Name { get; set; }

    public JsDownloadSource(string jsContent, string name)
    {
        host = new(options =>
        {
            options.UseWebApis().UseFetch(fetch => fetch.Timeout = TimeSpan.FromSeconds(120));
            options.Constraints.PromiseTimeout = TimeSpan.FromSeconds(120);
        });

        host.Execute(jsContent).Execute(ContractAdapter);
        Name = name;
    }

    public IReadOnlyList<LyricSourceInfoItem> GetSourceInfo()
    {
        var value = host.Invoke("__getSourceInfoJson");
        return JsDownloadSourceJson.DeserializeSourceInfo(value.AsString());
    }

    public async Task<ExternalLrcInfo[]> LookupLrcAsync(
        string title,
        string artist,
        CancellationToken cancellationToken = default)
    {
        await engineLock.WaitAsync(cancellationToken);
        try
        {
            var signal = host.WebApi.CreateAbortSignal(cancellationToken);
            var promise = host.Invoke("__lookupLrcJsonAsync", title, artist, signal);
            var value = await promise.UnwrapIfPromiseAsync(cancellationToken);
            return JsDownloadSourceJson.DeserializeLookup(value.AsString(), Name);
        }
        finally
        {
            engineLock.Release();
        }
    }

    public async Task<LyricDownload> DownloadLrcAsync(
        ExternalLrcInfo result,
        CancellationToken cancellationToken = default)
    {
        await engineLock.WaitAsync(cancellationToken);
        try
        {
            var resultJson = JsDownloadSourceJson.SerializeCandidate(result);
            var signal = host.WebApi.CreateAbortSignal(cancellationToken);
            var promise = host.Invoke("__downloadLrcJsonAsync", resultJson, signal);
            var value = await promise.UnwrapIfPromiseAsync(cancellationToken);
            return JsDownloadSourceJson.DeserializeDownload(value.AsString());
        }
        finally
        {
            engineLock.Release();
        }
    }

    public void Dispose()
    {
        engineLock.Dispose();
        host.Dispose();
    }
}