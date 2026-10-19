using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace LightStudio.LightPlayer.Web;

[SupportedOSPlatform("browser")]
internal static class BrowserAssets
{
    public static Task<JSObject> ImportAsync(string name, string path)
    {
        using var document = JSHost.GlobalThis.GetPropertyAsJSObject("document")
            ?? throw new InvalidOperationException("The browser document is unavailable.");
        var baseUri = document.GetPropertyAsString("baseURI")
            ?? throw new InvalidOperationException("The browser asset base is unavailable.");
        return JSHost.ImportAsync(name, new Uri(new Uri(baseUri), path).AbsoluteUri);
    }
}