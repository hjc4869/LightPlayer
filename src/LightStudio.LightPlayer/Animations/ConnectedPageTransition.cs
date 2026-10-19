using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using LightStudio.LightPlayer.ViewModels.Pages;

namespace LightStudio.LightPlayer.Animations;

internal abstract class ConnectedPageTransition : IPageTransition
{
    private static readonly AttachedProperty<double> ProgressProperty =
        AvaloniaProperty.RegisterAttached<ConnectedPageTransition, Control, double>("Progress");

    private readonly CancellationTokenSource cancellation = new();
    private readonly Dictionary<Visual, double> hidden = new();
    private readonly List<Visual> ancestors;
    private readonly TopLevel? topLevel;
    private readonly IPageTransition fallback;
    private IDisposable? outgoingPage;
    private PageTransitionSession? interactiveSession;
    private PageTransitionSession.VisualState? outgoingVisual;
    private PageTransitionSession.VisualState? incomingVisual;

    protected ConnectedPageTransition(Panel host, Control overlay, bool entering, IPageTransition fallback)
    {
        Host = host;
        Overlay = overlay;
        Entering = entering;
        this.fallback = fallback;
        overlay.IsHitTestVisible = false;
        overlay.ZIndex = 1000;
        if (host is Grid grid)
        {
            Grid.SetRowSpan(overlay, Math.Max(1, grid.RowDefinitions.Count));
            Grid.SetColumnSpan(overlay, Math.Max(1, grid.ColumnDefinitions.Count));
        }
        ancestors = host.GetVisualAncestors().Prepend(host).ToList();
        foreach (Visual ancestor in ancestors)
            ancestor.PropertyChanged += OnVisibilityChanged;
        host.SizeChanged += OnHostSizeChanged;
        host.DetachedFromVisualTree += OnHostDetached;
        topLevel = TopLevel.GetTopLevel(host);
        if (topLevel is not null)
            topLevel.ScalingChanged += OnScalingChanged;
        overlay.PropertyChanged += OnProgressChanged;
    }

    protected Panel Host { get; }
    protected Control Overlay { get; }
    protected bool Entering { get; }
    protected Visual? From { get; private set; }
    protected Visual? To { get; private set; }
    protected GeometryState Origin { get; private set; }
    protected GeometryState Destination { get; set; }
    protected bool Landed { get; private set; }
    protected bool IsInteractive { get; private set; }
    internal bool IsFinished { get; private set; }

    protected void Initialize(Control source, GeometryState origin)
    {
        Origin = origin;
        SetGeometry(origin);
        Hide(source);
        Host.Children.Add(Overlay);
    }

    protected void Hide(Visual visual)
    {
        hidden.TryAdd(visual, visual.Opacity);
        visual.Opacity = 0;
    }

    internal virtual void RetainOutgoingPage(PageViewModelBase page) => outgoingPage = page as IDisposable;

    internal async Task<PageTransitionSession> BeginInteractiveAsync(Visual? from, Visual? to,
        bool forward, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsFinished)
            throw new OperationCanceledException(cancellationToken);

        IsInteractive = true;
        From = from;
        To = to;
        outgoingVisual = from is null ? null : new PageTransitionSession.VisualState(from);
        incomingVisual = to is null ? null : new PageTransitionSession.VisualState(to);
        if (incomingVisual is not null)
            incomingVisual.Opacity = 0;

        var session = new PageTransitionSession(TimeSpan.FromMilliseconds(200), new CubicEaseOut(), Apply, commit =>
        {
            Landed = commit;
            Finish();
        });
        interactiveSession = session;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(session.Token, cancellationToken);
        try
        {
            OnStarting();
            await NextFrame(Host, linked.Token);
            await NextFrame(Host, linked.Token);
            bool connected = await PrepareAsync(linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (!connected)
            {
                session.Dispose();
                return PageTransitionSession.Begin(fallback, from, to, forward);
            }
            session.Update(0);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public async Task Start(Visual? from, Visual? to, bool forward, CancellationToken cancellationToken)
    {
        if (IsFinished)
            return;
        From = from;
        To = to;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, cancellationToken);
        CancellationToken token = linked.Token;
        try
        {
            if (from is not null)
            {
                from.IsVisible = true;
                from.RenderTransform = null;
            }
            if (to is not null)
            {
                to.IsVisible = true;
                to.Opacity = Entering ? 0 : 1;
                to.RenderTransform = null;
            }
            OnStarting();
            await NextFrame(Host, token);
            await NextFrame(Host, token);
            bool connected = await PrepareAsync(token);
            token.ThrowIfCancellationRequested();
            if (!connected)
            {
                RestoreVisuals();
                await fallback.Start(from, to, forward, token);
                return;
            }

            Apply(0);
            var animation = new Animation
            {
                Duration = TimeSpan.FromMilliseconds(200),
                Easing = new CubicEaseOut(),
                FillMode = FillMode.Forward,
                Children =
                {
                    new KeyFrame { Cue = new Cue(0), Setters = { new Setter(ProgressProperty, 0d) } },
                    new KeyFrame { Cue = new Cue(1), Setters = { new Setter(ProgressProperty, 1d) } },
                },
            };
            await animation.RunAsync(Overlay, token);
            token.ThrowIfCancellationRequested();
            Apply(1);
            Landed = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            Finish();
        }
    }

    protected virtual void OnStarting() { }
    protected abstract Task<bool> PrepareAsync(CancellationToken token);
    protected virtual void UpdateDestination() { }
    protected abstract void SetGeometry(GeometryState geometry);
    protected virtual void OnProgress(double progress) { }
    protected virtual void OnRemovingOverlay() { }
    protected virtual void OnRestoring() { }
    protected virtual void OnFinished() { }

    private void OnProgressChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (!IsFinished && args.Property == ProgressProperty)
            Apply(Overlay.GetValue(ProgressProperty));
    }

    private void Apply(double progress)
    {
        if (IsFinished)
            return;
        UpdateDestination();
        SetGeometry(GeometryState.Interpolate(Origin, Destination, progress));
        SetOutgoingOpacity(1 - progress);
        if (incomingVisual is not null)
            incomingVisual.Opacity = incomingVisual.OriginalOpacity * progress;
        else if (To is not null)
            To.Opacity = Entering ? progress : 1;
        OnProgress(progress);
    }

    protected void SetOutgoingOpacity(double opacity)
    {
        if (outgoingVisual is not null)
            outgoingVisual.Opacity = outgoingVisual.OriginalOpacity * opacity;
        else if (From is not null)
            From.Opacity = opacity;
    }

    internal void Cancel()
    {
        if (IsFinished)
            return;
        cancellation.Cancel();
        interactiveSession?.Dispose();
        Finish();
    }

    private void OnHostSizeChanged(object? sender, SizeChangedEventArgs args) => Cancel();
    private void OnHostDetached(object? sender, VisualTreeAttachmentEventArgs args) => Cancel();
    private void OnScalingChanged(object? sender, EventArgs args) => Cancel();

    private void OnVisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if ((args.Property == Visual.IsVisibleProperty || args.Property == Visual.OpacityProperty) &&
            (!Host.IsEffectivelyVisible || sender is Visual { Opacity: <= 0 }))
            Cancel();
    }

    private void RestoreVisuals()
    {
        OnRemovingOverlay();
        Host.Children.Remove(Overlay);
        foreach ((Visual visual, double opacity) in hidden)
            visual.Opacity = opacity;
        hidden.Clear();
        OnRestoring();
    }

    private void Finish()
    {
        if (IsFinished)
            return;
        IsFinished = true;
        RestoreVisuals();
        if (IsInteractive)
        {
            if (Landed)
            {
                outgoingVisual?.Complete(false);
                incomingVisual?.Complete(true);
            }
            else
            {
                outgoingVisual?.Dispose();
                incomingVisual?.Dispose();
            }
        }
        else if (From is not null)
        {
            From.Opacity = 1;
            From.IsVisible = false;
            From.RenderTransform = null;
        }
        if (!IsInteractive && To is not null)
        {
            To.Opacity = 1;
            To.RenderTransform = null;
        }
        foreach (Visual ancestor in ancestors)
            ancestor.PropertyChanged -= OnVisibilityChanged;
        Host.SizeChanged -= OnHostSizeChanged;
        Host.DetachedFromVisualTree -= OnHostDetached;
        if (topLevel is not null)
            topLevel.ScalingChanged -= OnScalingChanged;
        Overlay.PropertyChanged -= OnProgressChanged;
        OnFinished();
        outgoingPage?.Dispose();
        outgoingPage = null;
        cancellation.Dispose();
    }

    protected static async Task NextFrame(Visual owner, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (TopLevel.GetTopLevel(owner) is not { } topLevel)
            return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = token.Register(() => completion.TrySetCanceled(token));
        topLevel.RequestAnimationFrame(_ => completion.TrySetResult());
        await completion.Task;
    }

    internal static Rect? Map(Visual visual, Visual host, Rect rectangle)
    {
        if (TopLevel.GetTopLevel(host) is not { } topLevel ||
            visual.TransformToVisual(topLevel) is not { } source || topLevel.TransformToVisual(host) is not { } target)
            return null;
        return rectangle.TransformToAABB(source * target);
    }

    internal static Rect VisibleViewport(Visual visual, Visual host)
    {
        Rect viewport = new Rect(host.Bounds.Size);
        foreach (Visual ancestor in visual.GetVisualAncestors())
        {
            if ((ancestor.ClipToBounds || ancestor is ScrollViewer) &&
                Map(ancestor, host, new Rect(ancestor.Bounds.Size)) is { } clip)
                viewport = viewport.Intersect(clip);
            if (ReferenceEquals(ancestor, host))
                break;
        }
        return viewport;
    }

    internal readonly record struct GeometryState(Rect Image, Rect Clip, Rect Viewport, double Radius, double Rotation = 0)
    {
        public static GeometryState Interpolate(GeometryState from, GeometryState to, double progress) =>
            new(Interpolate(from.Image, to.Image, progress), Interpolate(from.Clip, to.Clip, progress),
                Interpolate(from.Viewport, to.Viewport, progress), from.Radius + (to.Radius - from.Radius) * progress,
                from.Rotation + (to.Rotation - from.Rotation) * progress);

        private static Rect Interpolate(Rect from, Rect to, double progress) => new(
            from.X + (to.X - from.X) * progress, from.Y + (to.Y - from.Y) * progress,
            from.Width + (to.Width - from.Width) * progress, from.Height + (to.Height - from.Height) * progress);
    }
}