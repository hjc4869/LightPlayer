using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace LightStudio.LightPlayer.Animations;

internal sealed class PageTransitionSession : Animatable, IDisposable
{
    private static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<PageTransitionSession, double>("Progress");

    private readonly Action<double> apply;
    private readonly Action<bool> finish;
    private readonly TimeSpan duration;
    private readonly Easing easing;
    private readonly CancellationTokenSource cancellation = new();
    private readonly CancellationToken token;
    private Visual? owner;
    private List<Visual>? ancestors;
    private bool settling;

    internal PageTransitionSession(TimeSpan duration, Easing easing, Action<double> apply, Action<bool> finish)
    {
        this.duration = duration;
        this.easing = easing;
        this.apply = apply;
        this.finish = finish;
        token = cancellation.Token;
    }

    internal bool IsFinished { get; private set; }
    internal bool IsCommitted { get; private set; }
    internal bool IsCancelled => IsFinished && !IsCommitted;
    internal CancellationToken Token => token;
    internal event Action? Cancelled;

    internal void Observe(Visual? visual)
    {
        if (visual is null)
            return;
        owner = visual;
        owner.DetachedFromVisualTree += OnDetached;
        ancestors = visual.GetSelfAndVisualAncestors().ToList();
        foreach (Visual ancestor in ancestors)
            ancestor.PropertyChanged += OnVisibilityChanged;
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs args) => Dispose();

    private void OnVisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (!IsFinished && sender is Visual visual &&
            (args.Property == Visual.IsVisibleProperty && !visual.IsVisible ||
             args.Property == Visual.OpacityProperty && !ReferenceEquals(visual, owner) && visual.Opacity <= 0))
            Dispose();
    }

    internal void Update(double progress)
    {
        if (!IsFinished && !settling && double.IsFinite(progress))
        {
            double value = Math.Clamp(progress, 0, 1);
            if (GetValue(ProgressProperty).Equals(value))
                apply(value);
            else
                SetValue(ProgressProperty, value);
        }
    }

    internal async Task SettleAsync(bool commit, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
        linked.Token.ThrowIfCancellationRequested();
        if (settling)
            throw new InvalidOperationException("The page transition is already settling.");
        settling = true;
        double progress = GetValue(ProgressProperty);
        double target = commit ? 1 : 0;
        if (duration <= TimeSpan.Zero || Math.Abs(target - progress) < 0.0001)
        {
            apply(target);
            return;
        }

        var animation = new Animation
        {
            Duration = TimeSpan.FromTicks(Math.Max(1, (long)(duration.Ticks * Math.Abs(target - progress)))),
            Easing = easing,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(ProgressProperty, progress) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(ProgressProperty, target) } },
            },
        };
        await animation.RunAsync(this, linked.Token);
        linked.Token.ThrowIfCancellationRequested();
        apply(target);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (!IsFinished && change.Property == ProgressProperty)
            apply(GetValue(ProgressProperty));
    }

    internal void Complete(bool commit)
    {
        if (IsFinished)
            return;
        IsFinished = true;
        IsCommitted = commit;
        if (owner is not null)
            owner.DetachedFromVisualTree -= OnDetached;
        if (ancestors is not null)
            foreach (Visual ancestor in ancestors)
                ancestor.PropertyChanged -= OnVisibilityChanged;
        cancellation.Cancel();
        try
        {
            finish(commit);
        }
        finally
        {
            cancellation.Dispose();
            if (!commit)
                Cancelled?.Invoke();
        }
    }

    public void Dispose() => Complete(false);

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        using (this)
        {
            try
            {
                await SettleAsync(true, cancellationToken);
                Complete(true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || token.IsCancellationRequested)
            {
            }
        }
    }

    internal static PageTransitionSession Begin(IPageTransition transition, Visual? from, Visual? to, bool forward) =>
        transition switch
        {
            SlidePageTransition slide => slide.Begin(from, to, forward),
            DrillInPageTransition drill => drill.Begin(from, to, forward),
            _ => new DrillInPageTransition { EnterScale = 1, ExitScale = 1 }.Begin(from, to, forward),
        };

    internal sealed class VisualState : AvaloniaObject, IDisposable
    {
        private static readonly StyledProperty<double> OpacityProperty =
            AvaloniaProperty.Register<VisualState, double>(nameof(Opacity), 1);

        private readonly Visual visual;
        private readonly IDisposable? transform;
        private readonly IDisposable? origin;
        private readonly IDisposable? visibility;
        private readonly IDisposable opacity;

        internal VisualState(Visual visual, ITransform? transform = null, bool centered = false)
        {
            this.visual = visual;
            OriginalOpacity = Opacity = visual.Opacity;
            if (transform is not null)
                this.transform = visual.SetValue(Visual.RenderTransformProperty, transform, BindingPriority.Animation);
            if (centered)
                origin = visual.SetValue(Visual.RenderTransformOriginProperty, RelativePoint.Center, BindingPriority.Animation);
            visibility = visual.SetValue(Visual.IsVisibleProperty, true, BindingPriority.Animation);
            opacity = visual.Bind(Visual.OpacityProperty, this.GetObservable(OpacityProperty), BindingPriority.Animation);
        }

        internal double OriginalOpacity { get; }
        internal double Opacity
        {
            get => GetValue(OpacityProperty);
            set => SetValue(OpacityProperty, value);
        }

        internal void Complete(bool visible)
        {
            visibility?.Dispose();
            visual.SetCurrentValue(Visual.IsVisibleProperty, visible);
            Dispose();
        }

        public void Dispose()
        {
            visibility?.Dispose();
            opacity.Dispose();
            transform?.Dispose();
            origin?.Dispose();
        }
    }
}