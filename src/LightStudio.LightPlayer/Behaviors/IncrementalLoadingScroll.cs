using System;
using System.ComponentModel;
using Avalonia.Controls;
using LightStudio.LightPlayer.ViewModels.Pages;

namespace LightStudio.LightPlayer.Behaviors;

/// <summary>Requests another page when a visible scroll viewer approaches its current extent.</summary>
internal sealed class IncrementalLoadingScroll
{
    private const double MinimumPrefetchDistance = 320;

    private readonly Control view;
    private readonly ScrollViewer scrollViewer;
    private INotifyPropertyChanged? viewModel;
    private bool isAttached;

    private IncrementalLoadingScroll(Control view, ScrollViewer scrollViewer)
    {
        this.view = view;
        this.scrollViewer = scrollViewer;
        scrollViewer.ScrollChanged += (_, _) => TryLoad(scrollViewer);
        scrollViewer.SizeChanged += (_, _) => TryLoad(scrollViewer);
        view.AttachedToVisualTree += (_, _) =>
        {
            isAttached = true;
            AttachViewModel();
        };
        view.DetachedFromVisualTree += (_, _) =>
        {
            isAttached = false;
            DetachViewModel();
        };
        view.DataContextChanged += (_, _) =>
        {
            if (isAttached)
            {
                AttachViewModel();
            }
        };
    }

    public static void Track(Control view, ScrollViewer scrollViewer) =>
        _ = new IncrementalLoadingScroll(view, scrollViewer);

    private void AttachViewModel()
    {
        DetachViewModel();

        viewModel = view.DataContext as INotifyPropertyChanged;
        if (viewModel is not null)
        {
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void DetachViewModel()
    {
        if (viewModel is not null)
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            viewModel = null;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(IIncrementalLoadingViewModel.CanLoadMore))
        {
            TryLoad(scrollViewer);
        }
    }

    private void TryLoad(ScrollViewer scrollViewer)
    {
        if (!scrollViewer.IsEffectivelyVisible ||
            scrollViewer.Viewport.Height <= 0 ||
            view.DataContext is not IIncrementalLoadingViewModel { CanLoadMore: true } viewModel)
        {
            return;
        }

        var loadedRatio = viewModel.TotalItemCount > 0
            ? Math.Clamp((double)viewModel.LoadedItemCount / viewModel.TotalItemCount, 0, 1)
            : 1;
        var loadedExtent = scrollViewer.Extent.Height * loadedRatio;
        var remaining = loadedExtent - scrollViewer.Offset.Y - scrollViewer.Viewport.Height;
        var prefetchDistance = Math.Max(MinimumPrefetchDistance, scrollViewer.Viewport.Height * 2);
        if (remaining <= prefetchDistance)
        {
            _ = viewModel.LoadMoreAsync();
        }
    }
}