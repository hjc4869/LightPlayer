using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Data;
using Avalonia.Threading;
using LightStudio.Logging;

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

/// <summary>
/// Presents the routed page and plays the transition between pages. The view of
/// <see cref="RetainedContent"/> stays in the visual tree, hidden, after another page replaces it,
/// so returning to that page shows the view as it was left instead of building a new one. At most
/// one view is kept, and any navigation that does not return to it releases it.
/// </summary>
public sealed class PageTransitionHost : ContentControl
{
    public static readonly StyledProperty<IPageTransition?> PageTransitionProperty =
        TransitioningContentControl.PageTransitionProperty.AddOwner<PageTransitionHost>();

    public static readonly StyledProperty<bool> IsTransitionReversedProperty =
        TransitioningContentControl.IsTransitionReversedProperty.AddOwner<PageTransitionHost>();

    /// <summary>
    /// Defines the <see cref="RetainedContent"/> property. It has to be set before
    /// <see cref="ContentControl.Content"/> moves away from the content it names.
    /// </summary>
    public static readonly StyledProperty<object?> RetainedContentProperty =
        AvaloniaProperty.Register<PageTransitionHost, object?>(nameof(RetainedContent));

    private ContentPresenter? first;
    private ContentPresenter? second;
    private ContentPresenter? current;
    private ContentPresenter? outgoing;
    private CancellationTokenSource? transition;
    private ContentPreview? preview;
    private bool transitionPending;
    private bool transitioning;

    protected override Type StyleKeyOverride => typeof(TransitioningContentControl);

    public IPageTransition? PageTransition
    {
        get => GetValue(PageTransitionProperty);
        set => SetValue(PageTransitionProperty, value);
    }

    public bool IsTransitionReversed
    {
        get => GetValue(IsTransitionReversedProperty);
        set => SetValue(IsTransitionReversedProperty, value);
    }

    /// <summary>Content whose view is kept, hidden, once other content replaces it.</summary>
    public object? RetainedContent
    {
        get => GetValue(RetainedContentProperty);
        set => SetValue(RetainedContentProperty, value);
    }

    internal ContentPresenter? CurrentPresenter =>
        current is { IsVisible: true } && ReferenceEquals(current.Content, Content) ? current : null;

    internal bool IsTransitioning => transitioning;

    protected override bool RegisterContentPresenter(ContentPresenter presenter)
    {
        if (presenter.Name == "PART_ContentPresenter2")
        {
            ResetPresenters();
            second = presenter;
            second.IsVisible = false;
            UpdateContent(false);
            return true;
        }

        if (!base.RegisterContentPresenter(presenter))
            return false;
        ResetPresenters();
        first = presenter;
        return true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (current is null || !ReferenceEquals(current.Content, Content))
            UpdateContent(false);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == ContentProperty)
        {
            if (preview is { IsCommitting: false })
                preview.Dispose();
            UpdateContent(true);
            return;
        }
        base.OnPropertyChanged(change);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs args)
    {
        preview?.Dispose();
        transitioning = false;
        base.OnDetachedFromVisualTree(args);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (!OperatingSystem.IsBrowser())
            return ArrangeAndStartTransition(finalSize);
        // Browser layout runs from animation frame callbacks, which have no synchronization context.
        using var context = AvaloniaSynchronizationContext.Ensure(Dispatcher.UIThread, DispatcherPriority.Normal);
        return ArrangeAndStartTransition(finalSize);
    }

    internal ContentPreview? Preview(object content)
    {
        if (transitioning || preview is not null || CurrentPresenter is not { } presenter ||
            (ReferenceEquals(presenter, first) ? second : first) is not { } destination)
            return null;

        preview = new ContentPreview(this, presenter, destination, content);
        return preview;
    }

    private void ResetPresenters()
    {
        preview?.Dispose();
        CancelTransition();
        current = null;
        outgoing = null;
        transitioning = false;
    }

    private void UpdateContent(bool withTransition)
    {
        if (VisualRoot is null || first is null || second is null)
            return;

        CancelTransition();
        object? content = Content;
        ContentPresenter? previous = current;
        ContentPresenter other = ReferenceEquals(previous, first) ? second : first;
        ContentPresenter target;
        if (previous is not null && ReferenceEquals(previous.Content, content))
            target = previous;
        else if (content is not null && ReferenceEquals(other.Content, content))
            target = other;
        else if (!withTransition && previous is not null)
            target = previous;
        else
            target = other;

        target.Content = content;
        target.IsVisible = true;
        current = target;
        outgoing = ReferenceEquals(previous, target) ? null : previous;
        transitioning = withTransition && outgoing is not null && PageTransition is not null;
        if (transitioning)
        {
            transitionPending = true;
            InvalidateArrange();
        }
        else
        {
            Settle();
        }
    }

    private Size ArrangeAndStartTransition(Size finalSize)
    {
        Size size = base.ArrangeOverride(finalSize);
        if (transitionPending)
        {
            transitionPending = false;
            StartTransition();
        }
        return size;
    }

    private void StartTransition()
    {
        if (outgoing is not { } from || current is not { } to || PageTransition is not { } pageTransition)
        {
            transitioning = false;
            Settle();
            return;
        }

        var cancellation = new CancellationTokenSource();
        transition = cancellation;
        pageTransition.Start(from, to, !IsTransitionReversed, cancellation.Token).ContinueWith(task =>
        {
            if (task.IsFaulted)
                AppLog.Error("Navigation", "A page transition failed.", task.Exception);
            if (cancellation.IsCancellationRequested || !ReferenceEquals(transition, cancellation))
                return;
            transition = null;
            transitioning = false;
            Settle();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void CancelTransition()
    {
        transitionPending = false;
        if (transition is { } cancellation)
        {
            transition = null;
            cancellation.Cancel();
        }
    }

    // Hides the presenter that is not showing the current content, keeping its view only when it
    // shows the retained content.
    private void Settle()
    {
        outgoing = null;
        if ((ReferenceEquals(current, first) ? second : first) is not { } presenter ||
            ReferenceEquals(presenter, preview?.To))
            return;
        presenter.IsVisible = false;
        if (presenter.Content is { } content && !ReferenceEquals(content, RetainedContent))
            presenter.Content = null;
    }

    internal sealed class ContentPreview : IDisposable
    {
        private readonly PageTransitionHost host;
        private readonly IDisposable? hitTesting;
        private readonly IDisposable? order;
        private readonly bool keepsContent;
        private bool disposed;

        internal ContentPreview(PageTransitionHost host, ContentPresenter from, ContentPresenter to, object content)
        {
            this.host = host;
            From = from;
            To = to;
            keepsContent = ReferenceEquals(to.Content, content);
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
                // A view the host kept before the preview stays kept.
                if (!keepsContent)
                    To.Content = null;
            }
            hitTesting?.Dispose();
            order?.Dispose();
            if (ReferenceEquals(host.preview, this))
                host.preview = null;
        }
    }
}
