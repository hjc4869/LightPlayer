using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using LightStudio.LightPlayer.Services;

namespace LightStudio.LightPlayer.Models;

/// <summary>
/// What the photo viewer is opened with: the picture to show and the ones it may be paged through
/// to, in the order the page that opened it listed them.
/// </summary>
/// <remarks>
/// The list travels with the request rather than being looked up again by the viewer, because what
/// counts as "the next picture" is a property of where the user came from -- a folder, the recently
/// viewed list, or a handful of files dropped on the window -- and only that page knows it.
/// </remarks>
public sealed record PhotoViewerRequest(IReadOnlyList<MediaReference> Photos, int Index)
{
    public static PhotoViewerRequest ForOne(MediaReference photo) => new([photo], 0);
}

public sealed class PhotoPreview
{
    private readonly Action release;
    private int references = 1;

    internal PhotoPreview(IImage image, Action release, bool isViewportPreview = false, Size naturalSize = default)
    {
        Image = image;
        this.release = release;
        IsViewportPreview = isViewportPreview;
        NaturalSize = naturalSize;
    }

    public IImage Image { get; }

    internal Size NaturalSize { get; }

    /// <summary>Whether the preview was decoded for the viewer's viewport rather than being a library thumbnail.</summary>
    internal bool IsViewportPreview { get; }

    internal PhotoPreview AddRef()
    {
        ObjectDisposedException.ThrowIf(references == 0, this);
        references++;
        return this;
    }

    internal void Release()
    {
        if (--references == 0)
            release();
    }

    internal IImage RetainImage() => new ImageLease(AddRef());

    private sealed class ImageLease : IImage, IDisposable
    {
        private PhotoPreview? retained;

        public ImageLease(PhotoPreview owner) => retained = owner;

        private PhotoPreview Owner => retained ?? throw new ObjectDisposedException(nameof(ImageLease));

        public Size Size => Owner.Image.Size;

        public void Draw(DrawingContext context, Rect sourceRect, Rect destRect) =>
            Owner.Image.Draw(context, sourceRect, destRect);

        public void Dispose()
        {
            retained?.Release();
            retained = null;
        }
    }
}

/// <summary>
/// What the photo viewer asks the shell to do on its behalf, for the actions that need a surface
/// the page itself does not own.
/// </summary>
public sealed record PhotoViewerCallbacks
{
    public Func<MediaReference, CancellationToken, Task<IImage?>>? LoadPreview { get; init; }
    public int PreviewMaxDimension { get; init; } = VideoPreviewFactory.PreviewMaxDimension;

    /// <summary>Leaves the page, which is how the shell takes it off the navigation stack.</summary>
    public required Action Close { get; init; }

    public Action<bool>? SetFullScreen { get; init; }

    public Func<string, bool>? TryOpenInNewWindow { get; init; }

    /// <summary>Shows the picture's file properties, or null on heads without a properties dialog.</summary>
    public Action<MediaReference, string>? ShowProperties { get; init; }

    /// <summary>
    /// Reveals a file in the platform file manager, reporting failure the way the rest of the shell
    /// does. Null where the head cannot show a folder at all.
    /// </summary>
    public Action<string>? RevealInFileManager { get; init; }
}
