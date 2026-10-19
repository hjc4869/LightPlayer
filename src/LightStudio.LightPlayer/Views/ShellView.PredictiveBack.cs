using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using LightStudio.LightPlayer.Animations;
using LightStudio.LightPlayer.Controls;
using LightStudio.LightPlayer.ViewModels;
using LightStudio.LightPlayer.ViewModels.Pages;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Views;

public partial class ShellView
{
    private BackGesture? backGesture;

    public bool TryBeginPredictiveBack(bool fromRightEdge = false)
    {
        CancelPredictiveBack();
        if (Shell is not { } shell || !CanPreviewBack(shell) || PageHost.IsTransitioning ||
            connectedTransition is { IsFinished: false })
            return false;

        MasterDetailView.BackPreview? detail = null;
        ShellViewModel.BackNavigation? navigation = null;
        if (shell.CurrentPage is IInPageBackHandler { CanHandleBack: true } handler)
        {
            if (fromRightEdge || !handler.CanPreviewBack)
                return false;
            Visual root = shell.ShowLibraryPager ? LibraryPagerHost : PageHost;
            detail = root.GetVisualDescendants().OfType<MasterDetailView>()
                .Select(view => view.BeginBackPreview(handler)).FirstOrDefault(preview => preview is not null);
            if (detail is null)
                return false;
        }
        else
        {
            navigation = shell.PrepareBackNavigation();
            if (navigation is null)
                return false;
            if (navigation.Transition is NoPageTransition or EntrancePageTransition || shell.ShowLibraryPager && navigation.UsesLibraryPager)
            {
                navigation.Dispose();
                return false;
            }
        }

        backGesture = new BackGesture(this, shell, navigation, detail, fromRightEdge);
        backGesture.Start();
        if (backGesture.WasDeclined)
        {
            backGesture = null;
            return false;
        }
        return true;
    }

    public void UpdatePredictiveBack(double progress) => backGesture?.Update(progress);

    public async Task CompletePredictiveBackAsync(bool commit)
    {
        if (backGesture is not { } gesture)
            return;
        try
        {
            await gesture.CompleteAsync(commit);
        }
        finally
        {
            if (ReferenceEquals(backGesture, gesture))
                backGesture = null;
            gesture.Dispose();
        }
    }

    public void CancelPredictiveBack()
    {
        BackGesture? gesture = backGesture;
        backGesture = null;
        gesture?.Dispose();
    }

    private void OnPredictiveBackContextRequested(object? sender, ContextRequestedEventArgs args)
    {
        if (backGesture is not null)
            args.Handled = true;
    }

            private void OnPredictiveBackInput(object? sender, RoutedEventArgs args) => CancelPredictiveBack();

    private bool CanPreviewBack(ShellViewModel shell) => IsEffectivelyVisible &&
        !shell.IsOverlayOpen && !shell.IsSearchActive && !shell.NowPlayingSheet.IsExpanded &&
        !shell.VideoSheet.IsExpanded && inputPane?.State != Avalonia.Controls.Platform.InputPaneState.Open &&
        backSubscribedTopLevel.FindDescendantOfType<OverlayPopupHost>(includeSelf: false,
            predicate: static popup => popup.IsVisible) is null;

    private sealed class BackGesture : IDisposable
    {
        private readonly ShellView owner;
        private readonly ShellViewModel shell;
        private readonly PageViewModelBase source;
        private readonly ShellViewModel.BackNavigation? navigation;
        private readonly MasterDetailView.BackPreview? detail;
        private readonly CancellationTokenSource cancellation = new();
        private readonly CancellationToken token;
        private readonly List<IDisposable> values = [];
        private readonly List<(ScrollViewer Viewer, Vector Offset)> scrollPositions = [];
        private readonly List<Visual> ancestors;
        private readonly bool fromPager;
        private readonly bool fromRightEdge;
        private PageTransitionHost.ContentPreview? content;
        private ConnectedPageTransition? connected;
        private PageTransitionSession? session;
        private Task ready = Task.CompletedTask;
        private double progress;
        private bool disposed;
        private bool completing;
        private bool committing;

        internal BackGesture(ShellView owner, ShellViewModel shell, ShellViewModel.BackNavigation? navigation,
            MasterDetailView.BackPreview? detail, bool fromRightEdge)
        {
            this.owner = owner;
            this.shell = shell;
            this.navigation = navigation;
            this.detail = detail;
            this.fromRightEdge = fromRightEdge;
            source = shell.CurrentPage;
            fromPager = shell.ShowLibraryPager;
            token = cancellation.Token;
            ancestors = owner.GetSelfAndVisualAncestors().ToList();
            foreach (Visual ancestor in ancestors)
                ancestor.PropertyChanged += OnVisibilityChanged;
            shell.PropertyChanged += OnStateChanged;
            source.PropertyChanged += OnStateChanged;
            shell.NowPlayingSheet.PropertyChanged += OnStateChanged;
            shell.VideoSheet.PropertyChanged += OnStateChanged;
        }

        private bool IsCurrent => !disposed && ReferenceEquals(owner.Shell, shell) &&
            ReferenceEquals(shell.CurrentPage, source) && owner.CanPreviewBack(shell) &&
            (navigation?.IsCurrent ?? detail?.IsCurrent ?? false) && content?.IsDisposed != true;

        internal bool WasDeclined { get; private set; }

        internal void Start() => ready = PrepareAsync();

        private async Task PrepareAsync()
        {
            try
            {
                if (detail is not null)
                {
                    session = detail.Session;
                }
                else if (navigation is not null)
                {
                    Visual? from = fromPager ? owner.LibraryPagerHost : owner.PageHost.CurrentPresenter;
                    if (from is null)
                    {
                        Dispose();
                        return;
                    }
                    connected = (ConnectedPageTransition?)PhotoPageTransition.Create(owner.ShellRoot,
                        source, navigation.To, navigation.Transition) ??
                        ArtworkPageTransition.Create(owner.ShellRoot, source, navigation.To, navigation.Transition);
                    owner.connectedTransition = connected;
                    if (fromRightEdge && connected is null && navigation.Transition is SlidePageTransition)
                    {
                        Decline();
                        return;
                    }
                    navigation.Present();

                    Visual to;
                    if (navigation.UsesLibraryPager)
                    {
                        if (owner.LibraryPagerHost.PreviewPage(navigation.To) is null)
                        {
                            Dispose();
                            return;
                        }
                        values.Add(owner.LibraryPagerPanel.SetValue(IsVisibleProperty, true, BindingPriority.Animation)!);
                        to = owner.LibraryPagerHost;
                        foreach (ScrollViewer scroll in to.GetVisualDescendants().OfType<ScrollViewer>())
                            scrollPositions.Add((scroll, scroll.Offset));
                    }
                    else
                    {
                        content = owner.PageHost.Preview(navigation.To);
                        if (content is null)
                        {
                            Dispose();
                            return;
                        }
                        to = content.To;
                        if (fromPager)
                            values.Add(owner.PageHost.SetValue(ZIndexProperty, -1, BindingPriority.Animation)!);
                    }

                    owner.UpdateLayout();
                    token.ThrowIfCancellationRequested();
                    session = connected is not null
                        ? await connected.BeginInteractiveAsync(from, to, forward: false, token)
                        : PageTransitionSession.Begin(navigation.Transition, from, to, forward: false);
                    if (fromRightEdge && connected?.IsFinished == true && navigation.Transition is SlidePageTransition)
                    {
                        Decline();
                        return;
                    }
                }

                token.ThrowIfCancellationRequested();
                if (!IsCurrent || session is null)
                {
                    Dispose();
                    return;
                }
                session.Cancelled += OnSessionCancelled;
                session.Update(progress);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested || connected?.IsFinished == true)
            {
                Dispose();
            }
            catch (Exception exception)
            {
                AppLog.Error("Navigation", "Preparing a predictive back transition failed.", exception);
                Dispose();
            }
            finally
            {
                if (disposed)
                    session?.Dispose();
            }
        }

        internal void Update(double value)
        {
            if (disposed || completing || !double.IsFinite(value))
                return;
            if (!IsCurrent)
            {
                Dispose();
                return;
            }
            progress = Math.Clamp(value, 0, 1);
            session?.Update(progress);
        }

        internal async Task CompleteAsync(bool commit)
        {
            if ((disposed && !WasDeclined) || completing)
                return;
            completing = true;
            try
            {
                await ready;
                if (WasDeclined)
                {
                    if (commit && ReferenceEquals(owner.Shell, shell) && owner.CanPreviewBack(shell) &&
                        navigation?.IsTargetCurrent == true)
                    {
                        if (ReferenceEquals(owner.backGesture, this))
                            owner.backGesture = null;
                        shell.TryGoBack();
                    }
                    return;
                }
                if (!IsCurrent || session is null)
                    return;
                await session.SettleAsync(commit, token);
                if (!commit || !IsCurrent)
                    return;

                committing = true;
                session.Complete(true);
                if (detail is not null)
                    detail.Commit();
                else if (navigation is not null)
                {
                    if (content is not null)
                        content.Commit(navigation.Commit);
                    else
                    {
                        using var transition = owner.PageHost.SetValue(PageTransitionHost.PageTransitionProperty,
                            null, BindingPriority.Animation);
                        navigation.Commit();
                    }
                    if (fromPager)
                        owner.LibraryPagerHost.IsVisible = true;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested || session?.IsCancelled == true)
            {
            }
            finally
            {
                Dispose();
            }
        }

        private void OnSessionCancelled() => Dispose();

        private void Decline()
        {
            WasDeclined = true;
            Dispose();
        }

        private void OnVisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if ((args.Property == IsVisibleProperty || args.Property == OpacityProperty) &&
                (!owner.IsEffectivelyVisible || sender is Visual { Opacity: <= 0 }))
                Dispose();
        }

        private void OnStateChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (!committing && !IsCurrent)
                Dispose();
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            cancellation.Cancel();
            foreach (Visual ancestor in ancestors)
                ancestor.PropertyChanged -= OnVisibilityChanged;
            shell.PropertyChanged -= OnStateChanged;
            source.PropertyChanged -= OnStateChanged;
            shell.NowPlayingSheet.PropertyChanged -= OnStateChanged;
            shell.VideoSheet.PropertyChanged -= OnStateChanged;
            if (session is not null)
            {
                session.Cancelled -= OnSessionCancelled;
                session.Dispose();
            }
            connected?.Cancel();
            if (ReferenceEquals(owner.connectedTransition, connected))
                owner.connectedTransition = null;
            content?.Dispose();
            detail?.Dispose();
            foreach (IDisposable value in values)
                value.Dispose();
            if (!committing)
                foreach ((ScrollViewer viewer, Vector offset) in scrollPositions)
                    viewer.Offset = offset;
            owner.LibraryPagerHost.RestorePreviewPage();
            navigation?.Dispose();
            cancellation.Dispose();
        }
    }
}