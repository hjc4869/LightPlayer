using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Threading;

namespace LightStudio.LightPlayer.Controls;

public class DispatcherTransitioningContentControl : TransitioningContentControl
{
    protected override Type StyleKeyOverride => typeof(TransitioningContentControl);

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (!OperatingSystem.IsBrowser())
            return base.ArrangeOverride(finalSize);
        using var context = AvaloniaSynchronizationContext.Ensure(Dispatcher.UIThread, DispatcherPriority.Normal);
        return base.ArrangeOverride(finalSize);
    }
}

public sealed class PageTransitionHost : DispatcherTransitioningContentControl
{
    private ContentPresenter? first;
    private ContentPresenter? second;
    private ContentPreview? preview;
    private bool transitioning;

    public PageTransitionHost()
    {
        TransitionCompleted += (_, args) =>
        {
            if (ReferenceEquals(args.Source, this) && ReferenceEquals(args.To, Content) &&
                (args.HasRunToCompletion || PageTransition is null))
                transitioning = false;
        };
    }

    protected override Type StyleKeyOverride => typeof(TransitioningContentControl);

    internal ContentPresenter? CurrentPresenter =>
        first is { IsVisible: true } && ReferenceEquals(first.Content, Content) ? first :
        second is { IsVisible: true } && ReferenceEquals(second.Content, Content) ? second : null;

    internal bool IsTransitioning => transitioning;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs args)
    {
        preview?.Dispose();
        base.OnApplyTemplate(args);
        first = args.NameScope.Find<ContentPresenter>("PART_ContentPresenter");
        second = args.NameScope.Find<ContentPresenter>("PART_ContentPresenter2");
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == ContentProperty)
        {
            if (preview is { IsCommitting: false })
                preview.Dispose();
            transitioning = VisualRoot is not null && PageTransition is not null;
        }
        base.OnPropertyChanged(change);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs args)
    {
        preview?.Dispose();
        transitioning = false;
        base.OnDetachedFromVisualTree(args);
    }

    internal ContentPreview? Preview(object content)
    {
        if (transitioning || preview is not null || CurrentPresenter is not { } current ||
            (ReferenceEquals(current, first) ? second : first) is not { } destination)
            return null;

        preview = new ContentPreview(this, current, destination, content);
        return preview;
    }

    internal sealed class ContentPreview : IDisposable
    {
        private readonly PageTransitionHost host;
        private readonly IDisposable? hitTesting;
        private readonly IDisposable? order;
        private bool disposed;

        internal ContentPreview(PageTransitionHost host, ContentPresenter from, ContentPresenter to, object content)
        {
            this.host = host;
            From = from;
            To = to;
            hitTesting = to.SetValue(IsHitTestVisibleProperty, false, BindingPriority.Animation);
            order = to.SetValue(ZIndexProperty, -1, BindingPriority.Animation);
            to.Content = content;
            to.IsVisible = true;
            to.UpdateChild();
        }

        internal ContentPresenter From { get; }
        internal ContentPresenter To { get; }
        internal bool IsCommitting { get; private set; }
        internal bool IsDisposed => disposed;

        internal bool Commit(Func<bool> commit)
        {
            if (disposed)
                return false;
            IsCommitting = true;
            using var transition = host.SetValue(PageTransitionProperty, null, BindingPriority.Animation);
            try
            {
                return commit();
            }
            finally
            {
                Dispose();
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            if (!IsCommitting || !ReferenceEquals(host.Content, To.Content))
            {
                To.IsVisible = false;
                To.Content = null;
            }
            hitTesting?.Dispose();
            order?.Dispose();
            if (ReferenceEquals(host.preview, this))
                host.preview = null;
        }
    }
}