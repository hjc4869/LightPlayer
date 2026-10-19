using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using LightStudio.LightPlayer.ViewModels.Pages;

namespace LightStudio.LightPlayer.Views;

/// <summary>
/// The picture on its own, for a host that shows it outside the app's window. Binds to the very
/// <see cref="VideoPlayerPageViewModel"/> the page it came from is still using, so the two views
/// share one pipeline and only the sink moves between them.
/// </summary>
public partial class PictureInPictureView : UserControl
{
    private readonly DispatcherTimer hideDeck = new() { Interval = TimeSpan.FromSeconds(2.5) };

    private VideoPlayerPageViewModel? model;
    private bool windowDragging;

    public PictureInPictureView()
    {
        InitializeComponent();

        SeekBar.SeekRequested += (_, e) => model?.Seek(e.Position);

        hideDeck.Tick += (_, _) => HideDeck();

        // Pointer-over cannot be relied on here: the browser's top level does not always report the
        // pointer leaving, so the deck is driven by movement and a timer instead.
        AddHandler(PointerMovedEvent, (_, _) => ShowDeck(), handledEventsToo: true);
        AddHandler(PointerPressedEvent, (_, _) => ShowDeck(), handledEventsToo: true);
        PointerExited += (_, _) => HideDeck();
        DataContextChanged += (_, _) => AttachModel();
        AttachedToVisualTree += (_, _) => ShowDeck();
        DetachedFromVisualTree += (_, _) => hideDeck.Stop();
    }

    /// <summary>
    /// Set while a finger is dragging the window about. A drag holds the pointer to the window, so
    /// no movement reaches this view to keep the deck up, while the framework goes on working out
    /// what the pointer is over from the last place it was seen: a window moving under a finger
    /// that reports nothing takes that place in and out of the picture over and over. The exits
    /// that come of it would otherwise put the deck away part way through the drag.
    /// </summary>
    public bool IsWindowDragging
    {
        get => windowDragging;
        set
        {
            if (windowDragging == value)
            {
                return;
            }

            windowDragging = value;

            // Up for the length of the drag, and the countdown begins again once it is over.
            ShowDeck();
        }
    }

    /// <summary>Takes the picture back from whichever view had it, once this one is on screen.</summary>
    public void ClaimVideo() => Video.ClaimVideo();

    /// <summary>Warms this top level before moving the live player into it.</summary>
    public void PrepareRenderTarget() => Video.PrepareRenderTarget();

    private void AttachModel()
    {
        if (model is not null)
        {
            model.PropertyChanged -= OnModelPropertyChanged;
        }

        model = DataContext as VideoPlayerPageViewModel;
        if (model is not null)
        {
            model.PropertyChanged += OnModelPropertyChanged;
        }
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The deck is left up while paused, so playing again is what starts its countdown.
        if (e.PropertyName == nameof(VideoPlayerPageViewModel.IsPlaying) && model is { IsPlaying: true })
        {
            ShowDeck();
        }
    }

    private void ShowDeck()
    {
        Deck.IsVisible = true;
        hideDeck.Stop();
        hideDeck.Start();
    }

    private void HideDeck()
    {
        hideDeck.Stop();

        // Left up while paused, as on the page: there is nothing to watch behind it. And left up
        // while the window is being dragged, see IsWindowDragging.
        if (!windowDragging && model is { IsPlaying: true })
        {
            Deck.IsVisible = false;
        }
    }
}
