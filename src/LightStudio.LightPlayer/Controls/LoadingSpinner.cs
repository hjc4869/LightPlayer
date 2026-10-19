using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls.Shapes;
using Avalonia.VisualTree;

namespace LightStudio.LightPlayer.Controls;

public sealed class LoadingSpinner : Ellipse
{
    private readonly List<Visual> ancestors = [];
    private bool inViewport;

    public LoadingSpinner()
    {
        Classes.Add("spinner");
        Classes.Add("active");
        StrokeDashArray = [21, 100];
        EffectiveViewportChanged += (_, eventArgs) =>
        {
            inViewport = eventArgs.EffectiveViewport.Intersects(new Rect(Bounds.Size));
            UpdateAnimation();
        };
    }

    protected override Type StyleKeyOverride => typeof(Ellipse);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs eventArgs)
    {
        base.OnAttachedToVisualTree(eventArgs);
        ancestors.AddRange(this.GetVisualAncestors());
        foreach (var ancestor in ancestors)
            ancestor.PropertyChanged += OnAncestorPropertyChanged;
        UpdateAnimation();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs eventArgs)
    {
        foreach (var ancestor in ancestors)
            ancestor.PropertyChanged -= OnAncestorPropertyChanged;
        ancestors.Clear();
        inViewport = false;
        Classes.Set("chromeVisible", false);
        base.OnDetachedFromVisualTree(eventArgs);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty || change.Property == OpacityProperty)
            UpdateAnimation();
    }

    private void OnAncestorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == IsVisibleProperty || change.Property == OpacityProperty)
            UpdateAnimation();
    }

    private void UpdateAnimation() => Classes.Set("chromeVisible",
        VisualRoot is not null && inViewport && IsVisible && Opacity > 0 &&
        ancestors.TrueForAll(ancestor => ancestor.IsVisible && ancestor.Opacity > 0));
}