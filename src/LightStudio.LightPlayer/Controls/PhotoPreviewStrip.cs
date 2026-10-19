using LightMediaRenderer.Avalonia.Controls;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.ViewModels.Pages;

namespace LightStudio.LightPlayer.Controls;

public sealed class PhotoPreviewStrip : Canvas
{
    private const double ThumbnailWidth = 64;
    private const double ThumbnailHeight = 48;
    private const double ThumbnailPitch = 70;
    private const double DragThreshold = 6;
    private readonly Dictionary<int, Thumbnail> thumbnails = new();
    private readonly List<Visual> visibilityAncestors = new();
    private readonly Glide centerGlide;
    private readonly TouchpadSwipe touchpadSwipe;
    private PhotoViewerPageViewModel? model;
    private IPointer? pointer;
    private Point pressPosition;
    private int pressedIndex = -1;
    private double position;
    private double startPosition;
    private bool selecting;

    public PhotoPreviewStrip()
    {
        ClipToBounds = true;
        Focusable = true;
        centerGlide = new Glide(this, SetPosition);
        touchpadSwipe = new TouchpadSwipe(this,
            () => IsEffectivelyVisible && model is { PhotoCount: > 1 } && pointer is null,
            OnTouchpadMoved, (_, _) => FinishInteraction());
        DataContextChanged += (_, _) => AttachModel(VisualRoot is null ? null : DataContext as PhotoViewerPageViewModel);
        AddHandler(PointerPressedEvent, OnStripPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnStripMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnStripReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerWheelChangedEvent, OnStripWheel, RoutingStrategies.Bubble);
        PointerCaptureLost += (_, args) =>
        {
            if (pointer == args.Pointer)
                FinishInteraction();
        };
        EffectiveViewportChanged += (_, args) =>
        {
            Rect visible = args.EffectiveViewport.Intersect(new Rect(Bounds.Size));
            if (!IsEffectivelyVisible || visible.Width <= 0 || visible.Height <= 0)
                Suspend();
            else
                RefreshThumbnails();
        };
    }

    public event Action? Activity;

    public event Action? SelectionStarted;

    public bool IsInteracting => pointer is not null || selecting || touchpadSwipe.IsTracking;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs args)
    {
        base.OnAttachedToVisualTree(args);
        foreach (Visual ancestor in this.GetVisualAncestors())
        {
            ancestor.PropertyChanged += OnAncestorPropertyChanged;
            visibilityAncestors.Add(ancestor);
        }
        AttachModel(DataContext as PhotoViewerPageViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs args)
    {
        AttachModel(null);
        foreach (Visual ancestor in visibilityAncestors)
            ancestor.PropertyChanged -= OnAncestorPropertyChanged;
        visibilityAncestors.Clear();
        base.OnDetachedFromVisualTree(args);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && centerGlide is not null)
            UpdateVisibility();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs args)
    {
        base.OnSizeChanged(args);
        touchpadSwipe.Reset();
        FinishInteraction(animate: false);
        SetPosition(model?.SelectedIndex ?? 0);
    }

    private void OnAncestorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == IsVisibleProperty)
            UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        if (!IsEffectivelyVisible || VisualRoot is null)
            Suspend();
        else
            SetPosition(model?.SelectedIndex ?? 0);
    }

    private void Suspend()
    {
        touchpadSwipe.Reset();
        FinishInteraction(animate: false);
        centerGlide.Stop();
        ClearThumbnails();
    }

    private void AttachModel(PhotoViewerPageViewModel? value)
    {
        Suspend();
        if (model is not null)
            model.PropertyChanged -= OnModelPropertyChanged;
        model = value;
        if (model is not null)
            model.PropertyChanged += OnModelPropertyChanged;
        SetPosition(model?.SelectedIndex ?? 0);
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(PhotoViewerPageViewModel.SelectedIndex) && !selecting)
        {
            centerGlide.Stop();
            SetPosition(model?.SelectedIndex ?? 0);
        }
        else if (args.PropertyName == nameof(PhotoViewerPageViewModel.Preview) && model is not null &&
            thumbnails.TryGetValue(model.SelectedIndex, out Thumbnail? thumbnail) && thumbnail.Preview is null &&
            model.RetainPreview(model.SelectedIndex) is { } preview)
        {
            thumbnail.Load.Cancel();
            thumbnail.SetPreview(preview);
        }
    }

    private void SetPosition(double value)
    {
        position = Math.Clamp(value, 0, Math.Max(0, (model?.PhotoCount ?? 1) - 1));
        RefreshThumbnails();
    }

    private void RefreshThumbnails()
    {
        if (model is null || VisualRoot is null || !IsEffectivelyVisible || Bounds.Width <= 0)
            return;

        double radius = Bounds.Width / (2 * ThumbnailPitch);
        int first = Math.Max(0, (int)Math.Floor(position - radius) - 1);
        int last = Math.Min(model.PhotoCount - 1, (int)Math.Ceiling(position + radius) + 1);
        foreach (int target in new List<int>(thumbnails.Keys))
        {
            if (target >= first && target <= last)
                continue;
            Thumbnail removed = thumbnails[target];
            Children.Remove(removed.Button);
            removed.Dispose();
            thumbnails.Remove(target);
        }

        for (int target = first; target <= last; target++)
        {
            if (!thumbnails.TryGetValue(target, out Thumbnail? thumbnail))
            {
                thumbnail = CreateThumbnail(target);
                thumbnails.Add(target, thumbnail);
                Children.Add(thumbnail.Button);
                if (model.RetainPreview(target) is { } retained)
                    thumbnail.SetPreview(retained);
                else
                {
                    Thumbnail loading = thumbnail;
                    _ = model.LoadThumbnailAsync(target, thumbnail.Load.Token, loading.SetPreview);
                }
            }

            thumbnail.Button.Classes.Set("selected", target == model.SelectedIndex);
            SetLeft(thumbnail.Button, (Bounds.Width - ThumbnailWidth) / 2 + (target - position) * ThumbnailPitch);
            SetTop(thumbnail.Button, (Bounds.Height - ThumbnailHeight) / 2);
        }
    }

    private Thumbnail CreateThumbnail(int target)
    {
        var image = new Image { Stretch = Stretch.UniformToFill };
        var placeholder = new Path
        {
            Width = 20,
            Height = 20,
            Stretch = Stretch.Uniform,
            Fill = Brushes.LightGray,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (this.TryFindResource("IconImage", out object? resource) && resource is Geometry geometry)
            placeholder.Data = geometry;
        var content = new Grid { Children = { placeholder, image } };
        var button = new Button
        {
            Width = ThumbnailWidth,
            Height = ThumbnailHeight,
            Focusable = false,
            Content = new Border { CornerRadius = new CornerRadius(2), ClipToBounds = true, Child = content },
            Tag = target,
        };
        button.Classes.Add("photoThumbnail");
        AutomationProperties.SetName(button, model!.GetPhotoName(target));
        ToolTip.SetTip(button, model.GetPhotoName(target));
        button.Click += OnThumbnailClicked;
        return new Thumbnail(button, image, placeholder);
    }

    private void OnThumbnailClicked(object? sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: int target })
            Select(target);
    }

    private void Select(int target)
    {
        if (model is null || target < 0 || target >= model.PhotoCount)
            return;
        SelectionStarted?.Invoke();
        model.SelectPhoto(target, thumbnails.GetValueOrDefault(target)?.Preview);
        SetPosition(model.SelectedIndex);
        Activity?.Invoke();
    }

    private int HitIndex(Point point)
    {
        double target = position + (point.X - Bounds.Width / 2) / ThumbnailPitch;
        int nearest = (int)Math.Round(target);
        return Math.Abs(target - nearest) * ThumbnailPitch <= ThumbnailWidth / 2 &&
            Math.Abs(point.Y - Bounds.Height / 2) <= ThumbnailHeight / 2 &&
            nearest >= 0 && nearest < model?.PhotoCount ? nearest : -1;
    }

    private void OnStripPressed(object? sender, PointerPressedEventArgs args)
    {
        if (pointer is not null || model is null || !args.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        Point point = args.GetPosition(this);
        int target = HitIndex(point);
        if (target < 0)
            return;
        centerGlide.Stop();
        pointer = args.Pointer;
        pressPosition = point;
        pressedIndex = target;
        startPosition = position;
        pointer.Capture(this);
        Focus();
        Activity?.Invoke();
        args.Handled = true;
    }

    private void OnStripMoved(object? sender, PointerEventArgs args)
    {
        if (pointer != args.Pointer)
            return;

        if (args.Pointer.Type != PointerType.Touch)
        {
            Activity?.Invoke();
            args.Handled = true;
            return;
        }

        double travel = args.GetPosition(this).X - pressPosition.X;
        if (!selecting && Math.Abs(travel) >= DragThreshold)
            BeginSelection();
        if (selecting)
        {
            args.PreventGestureRecognition();
            PreviewPosition(startPosition - travel / ThumbnailPitch);
        }
        Activity?.Invoke();
        args.Handled = true;
    }

    private void OnStripReleased(object? sender, PointerReleasedEventArgs args)
    {
        if (pointer != args.Pointer)
            return;
        int target = !selecting && HitIndex(args.GetPosition(this)) == pressedIndex ? pressedIndex : -1;
        FinishInteraction();
        if (target >= 0)
            Select(target);
        Activity?.Invoke();
        args.Handled = true;
    }

    private void BeginSelection()
    {
        if (selecting || model is null)
            return;
        selecting = true;
        centerGlide.Stop();
        SelectionStarted?.Invoke();
        if (thumbnails.GetValueOrDefault(model.SelectedIndex)?.Preview is { } preview)
            model.SupplyPreview(preview.AddRef());
    }

    private void PreviewPosition(double value)
    {
        SetPosition(value);
        int target = (int)Math.Round(position);
        // Full-resolution frames are kept until the drag reaches another photo.
        if (model is not null && target != model.SelectedIndex)
            model.BeginPreviewSelection();
        model?.SelectPhoto(target, thumbnails.GetValueOrDefault(target)?.Preview);
        RefreshThumbnails();
    }

    private void OnTouchpadMoved(double travel)
    {
        if (!selecting)
        {
            Background = Brushes.Transparent;
            startPosition = position;
            BeginSelection();
        }
        PreviewPosition(startPosition - travel / ThumbnailPitch);
        Activity?.Invoke();
    }

    private void OnStripWheel(object? sender, PointerWheelEventArgs args)
    {
#if LMR_AVALONIA_FORK
        if (args.IsTouchpad)
            return;
#endif
        if (args.KeyModifiers != KeyModifiers.None || IsInteracting || model is null)
            return;
        double delta = args.Delta.Y != 0 ? args.Delta.Y : args.Delta.X;
        if (!double.IsFinite(delta) || delta == 0)
            return;
        Select(model.SelectedIndex - Math.Sign(delta));
        args.Handled = true;
    }

    private void FinishInteraction(bool animate = true)
    {
        Background = null;
        IPointer? released = pointer;
        pointer = null;
        pressedIndex = -1;
        if (released?.Captured == this)
            released.Capture(null);
        if (selecting)
        {
            selecting = false;
            model?.EndPreviewSelection();
        }
        centerGlide.Stop();
        double target = model?.SelectedIndex ?? 0;
        if (animate && VisualRoot is not null && IsEffectivelyVisible && Math.Abs(target - position) > 0.001)
            centerGlide.Start(position, target, 140);
        else
            SetPosition(target);
    }

    private void ClearThumbnails()
    {
        Children.Clear();
        foreach (Thumbnail thumbnail in thumbnails.Values)
            thumbnail.Dispose();
        thumbnails.Clear();
    }

    private sealed class Thumbnail(Button button, Image image, Path placeholder) : IDisposable
    {
        public Button Button { get; } = button;
        public CancellationTokenSource Load { get; } = new();
        public PhotoPreview? Preview { get; private set; }

        public void SetPreview(PhotoPreview value)
        {
            image.Source = value.Image;
            placeholder.IsVisible = false;
            Preview?.Release();
            Preview = value;
        }

        public void Dispose()
        {
            Load.Cancel();
            Load.Dispose();
            image.Source = null;
            Preview?.Release();
            Preview = null;
        }
    }
}