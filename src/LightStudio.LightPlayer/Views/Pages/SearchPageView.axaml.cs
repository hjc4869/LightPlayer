using System.ComponentModel;
using Avalonia.Controls;
using LightStudio.LightPlayer.Behaviors;
using LightStudio.LightPlayer.ViewModels.Pages;

namespace LightStudio.LightPlayer.Views.Pages;

public partial class SearchPageView : UserControl
{
    private SearchPageViewModel? observedPage;
    private bool attached;

    public SearchPageView()
    {
        InitializeComponent();
        BrowseScrollMemory.Track(this, ResultsScrollViewer);
        DataContextChanged += (_, _) =>
        {
            ObservePage();
            UpdatePreviewWidth();
        };
        AttachedToVisualTree += (_, _) =>
        {
            attached = true;
            ObservePage();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            attached = false;
            ObservePage();
        };
    }

    private void ObservePage()
    {
        if (observedPage is not null)
            observedPage.PropertyChanged -= OnPagePropertyChanged;
        observedPage = attached ? DataContext as SearchPageViewModel : null;
        if (observedPage is not null)
            observedPage.PropertyChanged += OnPagePropertyChanged;
    }

    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SearchPageViewModel.Keyword))
            ResultsScrollViewer.Offset = default;
    }

    private void OnResultsSizeChanged(object? sender, SizeChangedEventArgs args) => UpdatePreviewWidth();

    private void UpdatePreviewWidth()
    {
        if (DataContext is SearchPageViewModel page)
            page.SetPreviewWidth(ResultsContent.Bounds.Width - 10);
    }
}
