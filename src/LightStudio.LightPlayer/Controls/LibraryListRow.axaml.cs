using LightMediaRenderer.Avalonia.Controls;
using System;
using Avalonia;
using Avalonia.Controls;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.Controls;

/// <summary>
/// A single album/artist row for the library list layout: media thumbnail (or artist collage) to
/// the left, two-line title/subtitle to the right, plus a right-click play/queue menu. Drag onto
/// the now-playing queue is handled by the hosting list (see <c>ListItemsPageView</c>) because the
/// rows are selectable list items.
/// </summary>
public partial class LibraryListRow : UserControl
{
    private LibraryTileModel? boundRow;
    private LibraryTileModel? artHolder;
    private bool attached;

    public LibraryListRow()
    {
        InitializeComponent();
        AddHandler(FastImage.SourceLostEvent, (_, _) => artHolder?.ReloadArt());
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        boundRow = DataContext as LibraryTileModel;
        UpdateArtHold();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        attached = true;
        UpdateArtHold();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        attached = false;
        UpdateArtHold();
    }

    // Exactly one artwork hold per row control, taken while it is bound and on screen. Rows are
    // recycled by the virtualizing list and torn down when scrolled out of the realized window, so
    // dropping the hold there is what keeps decoded bitmaps from piling up; the item itself only
    // frees its artwork once no control holds it any more.
    private void UpdateArtHold()
    {
        var target = attached ? boundRow : null;
        if (ReferenceEquals(artHolder, target))
        {
            return;
        }

        artHolder?.ReleaseArt();
        artHolder = target;
        artHolder?.RequestArt(1, (int)Math.Ceiling(ArtHost.Width * (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1)),
            FastImage.GetCachePool(this));
    }
}
