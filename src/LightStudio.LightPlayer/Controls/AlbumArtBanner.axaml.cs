using System;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Reactive;

namespace LightStudio.LightPlayer.Controls;

/// <summary>
/// Wide album-art strip shown by pages that drop their side cover column when they
/// get narrow. The album title and its metadata sit at the bottom of the artwork
/// over a fade to the page background, so the top of the cover stays fully visible
/// while the text below it stays readable.
/// </summary>
public partial class AlbumArtBanner : UserControl
{
    // Alpha ramp of the fade, top to bottom: the upper half of the artwork is left
    // untouched, then the page background ramps in to back the title and metadata.
    private static readonly (double Offset, double Alpha)[] FadeStops = new[]
    {
        (0.00, 0.00),
        (0.50, 0.45),
        (0.70, 0.65),
        (0.86, 0.85),
        (1.00, 1.00),
    };

    public static readonly StyledProperty<IImage?> SourceProperty =
        AvaloniaProperty.Register<AlbumArtBanner, IImage?>(nameof(Source));

    public static readonly StyledProperty<string?> HeaderProperty =
        AvaloniaProperty.Register<AlbumArtBanner, string?>(nameof(Header));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<AlbumArtBanner, string?>(nameof(Title));

    public static readonly StyledProperty<string?> ArtistNameProperty =
        AvaloniaProperty.Register<AlbumArtBanner, string?>(nameof(ArtistName));

    public static readonly StyledProperty<ICommand?> ArtistCommandProperty =
        AvaloniaProperty.Register<AlbumArtBanner, ICommand?>(nameof(ArtistCommand));

    public static readonly StyledProperty<string?> MetaProperty =
        AvaloniaProperty.Register<AlbumArtBanner, string?>(nameof(Meta));

    public static readonly StyledProperty<object?> ActionContentProperty =
        AvaloniaProperty.Register<AlbumArtBanner, object?>(nameof(ActionContent));

    private IDisposable? fadeSubscription;

    public AlbumArtBanner()
    {
        InitializeComponent();
    }

    /// <summary>The album artwork; a placeholder is drawn when null.</summary>
    public IImage? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>Optional accent-colored label above the title; hidden when empty.</summary>
    public string? Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    /// <summary>Album title drawn at the bottom of the artwork.</summary>
    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Optional artist line; hidden when empty.</summary>
    public string? ArtistName
    {
        get => GetValue(ArtistNameProperty);
        set => SetValue(ArtistNameProperty, value);
    }

    /// <summary>Command invoked when the artist line is tapped.</summary>
    public ICommand? ArtistCommand
    {
        get => GetValue(ArtistCommandProperty);
        set => SetValue(ArtistCommandProperty, value);
    }

    /// <summary>Optional secondary line (track/duration summary, year, ...).</summary>
    public string? Meta
    {
        get => GetValue(MetaProperty);
        set => SetValue(MetaProperty, value);
    }

    /// <summary>
    /// Album-level action buttons drawn at the bottom-right of the artwork. Pages move
    /// their own header button row in here while the banner is shown, so the buttons
    /// (and their bindings) only exist once.
    /// </summary>
    public object? ActionContent
    {
        get => GetValue(ActionContentProperty);
        set => SetValue(ActionContentProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // The page background is both theme-variant and user-palette driven, so track
        // the resource rather than baking a color into the gradient.
        fadeSubscription?.Dispose();
        fadeSubscription = this.GetResourceObservable("AppPageBackgroundBrush")
            .Subscribe(new AnonymousObserver<object?>(value => ApplyFade(value as ISolidColorBrush)));
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        fadeSubscription?.Dispose();
        fadeSubscription = null;
    }

    private void ApplyFade(ISolidColorBrush? pageBackground)
    {
        // Named fields are null until InitializeComponent runs.
        if (FadeOverlay is null || pageBackground is null)
        {
            return;
        }

        var color = pageBackground.Color;
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        };

        foreach (var (offset, alpha) in FadeStops)
        {
            // Only the alpha varies across the stops; keeping the RGB constant avoids
            // the muddy interpolation a fade from Colors.Transparent would produce.
            var stopColor = Color.FromArgb((byte)Math.Round(alpha * 255), color.R, color.G, color.B);
            brush.GradientStops.Add(new GradientStop(stopColor, offset));
        }

        FadeOverlay.Background = brush;
    }
}
