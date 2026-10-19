using System;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Avalonia.Browser;
using Avalonia.Threading;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Views;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Web;

/// <summary>
/// Detaches the picture into a document picture-in-picture window. Avalonia renders a second view
/// into a div in that window's document, which is a top level of its own with a canvas and a WebGL
/// context of its own; only the player's video sink moves between the two, so playback itself never
/// stops.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed partial class BrowserPictureInPicture : IPictureInPictureHost
{
    /// <summary>Width the window is asked for; its height follows the picture's shape.</summary>
    private const double RequestedWidth = 480d;

    private static readonly BrowserPictureInPicture Instance = new();

    /// <summary>
    /// Made once and kept: Avalonia's view has no way to be torn down, and browsers allow only so
    /// many live WebGL contexts.
    /// </summary>
    private AvaloniaView? view;

    private PictureInPictureView? content;
    private bool active;
    private bool busy;

    public static void Register()
    {
        if (IsSupported())
        {
            PlatformServices.PictureInPicture = Instance;
        }
    }

    public PictureInPictureKind Kind => PictureInPictureKind.DetachedView;

    public bool IsAvailable => IsSupported();

    public bool IsActive => active;

    public event Action<bool>? ActiveChanged;

    public void Enter(PictureInPictureRequest request)
    {
        if (active || busy)
        {
            return;
        }

        busy = true;
        _ = EnterAsync(request);
    }

    public void Exit()
    {
        if (active)
        {
            _ = CloseAsync();
        }
    }

    private async Task EnterAsync(PictureInPictureRequest request)
    {
        try
        {
            double aspect = request.AspectRatio > 0.1 ? request.AspectRatio : 16d / 9d;
            double height = Math.Clamp(RequestedWidth / aspect, 160d, 480d);
            JSObject? element = await RequestWindowAsync(RequestedWidth, height);
            if (element is null)
            {
                // Refused, most likely for want of a recent user gesture. The page is waiting to be
                // told the picture left, so tell it that it never did.
                Abandon();
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() => Prepare(element));
            await Dispatcher.UIThread.InvokeAsync(() => Show(request.DataContext));

            // The window is closed by its own frame as often as by the app, and either way the page
            // has to hear about it.
            await WaitForCloseAsync();
            await Dispatcher.UIThread.InvokeAsync(Hide);
            busy = false;
        }
        catch (Exception ex)
        {
            AppLog.Warning("Web.PictureInPicture", "Could not detach the picture.", ex);
            Abandon();
        }
    }

    private void Abandon() => Dispatcher.UIThread.Post(() =>
    {
        busy = false;
        if (!active)
        {
            ActiveChanged?.Invoke(false);
        }
    });

    private void Prepare(JSObject element)
    {
        view ??= new AvaloniaView(element);
        content = new PictureInPictureView();
        view.Content = content;
        content.PrepareRenderTarget();
    }

    private void Show(object dataContext)
    {
        if (content is null)
            throw new InvalidOperationException("The picture-in-picture view was not prepared.");

        content.DataContext = dataContext;
        active = true;
        busy = false;

        content.ClaimVideo();
        ActiveChanged?.Invoke(true);
    }

    private void Hide()
    {
        active = false;
        busy = true;
        if (view is not null)
        {
            view.Content = null;
        }

        // Dropping the binding source is what makes the view let go of the player: it unhooks its
        // events, gives up the sink and releases the frame it is holding.
        if (content is not null)
        {
            content.DataContext = null;
            content = null;
        }

        ActiveChanged?.Invoke(false);
    }

    [JSImport("isSupported", "BrowserPictureInPicture")]
    private static partial bool IsSupported();

    [JSImport("requestWindow", "BrowserPictureInPicture")]
    private static partial Task<JSObject?> RequestWindowAsync(double width, double height);

    [JSImport("waitForClose", "BrowserPictureInPicture")]
    private static partial Task WaitForCloseAsync();

    [JSImport("close", "BrowserPictureInPicture")]
    private static partial Task CloseAsync();
}
