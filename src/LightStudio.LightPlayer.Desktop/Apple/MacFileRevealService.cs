using System;
using System.IO;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Services;

namespace LightStudio.LightPlayer.Desktop.Apple;

/// <summary>
/// macOS implementation of <see cref="IFileRevealService"/>. Finder opens the folder with the track
/// already selected, matching its "Show in Finder" behavior.
/// </summary>
internal sealed unsafe class MacFileRevealService : IFileRevealService
{
    private static readonly DesktopFileRevealService FolderFallback = new();

    public Task RevealAsync(string filePath)
    {
        // A file that has since been moved or deleted still gets its folder opened.
        if (!File.Exists(filePath))
        {
            return FolderFallback.RevealAsync(filePath);
        }

        var path = ObjC.CreateString(filePath);
        var url = IntPtr.Zero;
        var urls = IntPtr.Zero;
        try
        {
            url = ObjC.Send(
                ObjC.Send(ObjC.GetClass("NSURL"), ObjC.Sel("alloc")),
                ObjC.Sel("initFileURLWithPath:"),
                path);
            urls = ObjC.Send(
                ObjC.Send(ObjC.GetClass("NSArray"), ObjC.Sel("alloc")),
                ObjC.Sel("initWithObjects:count:"),
                (IntPtr)(&url),
                (IntPtr)1);
            var workspace = ObjC.Send(ObjC.GetClass("NSWorkspace"), ObjC.Sel("sharedWorkspace"));
            ObjC.Send(workspace, ObjC.Sel("activateFileViewerSelectingURLs:"), urls);
        }
        finally
        {
            if (urls != IntPtr.Zero)
            {
                ObjC.Send(urls, ObjC.Sel("release"));
            }

            if (url != IntPtr.Zero)
            {
                ObjC.Send(url, ObjC.Sel("release"));
            }

            ObjC.CFRelease(path);
        }

        return Task.CompletedTask;
    }
}
