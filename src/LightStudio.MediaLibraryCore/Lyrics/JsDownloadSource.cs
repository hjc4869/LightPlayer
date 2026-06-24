using Jint;
using Jint.Native;
using LightStudio.MediaLibraryCore.Lyrics.RuntimeApi;
using System;
using System.Reflection;
using System.Threading.Tasks;

namespace LightStudio.MediaLibraryCore.Lyrics;

public class JsDownloadSource : IDisposable
{
    private readonly Engine host;
    private readonly JsApi api = new();
    private readonly XMLHttpRequest xml = new();

    public string Name { get; set; }

    public JsDownloadSource(string jsContent, string name, Assembly[] assemblies)
    {
        host = new(cfg => cfg.AllowClr(assemblies));
        host.SetValue("xmlhttp", xml);
        host.SetValue("api", api);
        host.Execute(jsContent);
        Name = name;
    }

    public ExternalLrcInfo[] LookupLrc(string Title, string Artist)
    {
        lock (host)
        {
            try
            {
                host.Call("lookupLrc", Title, Artist);
                var lrcs = api.GetLrcs();
                for (int i = 0; i < lrcs.Length; i++) lrcs[i].Source = Name;
                return lrcs;
            }
            catch
            {
                //ignore
                return ExternalLrcInfo.EmptyArray;
            }
        }
    }

    public async Task<ExternalLrcInfo[]> LookupLrcAsync(string Title, string Artist)
    {
        return await Task.Run(() => LookupLrc(Title, Artist));
    }

    public string DownloadLrc(ExternalLrcInfo result)
    {
        lock (host)
        {
            return host.Call("downloadLrc", JsValue.FromObjectWithType(host, result, typeof(ExternalLrcInfo))).AsString();
        }
    }

    public async Task<string> DownloadLrcAsync(ExternalLrcInfo result)
    {
        return await Task.Run(() => DownloadLrc(result));
    }

    public void Dispose()
    {
        host.Dispose();
    }
}
