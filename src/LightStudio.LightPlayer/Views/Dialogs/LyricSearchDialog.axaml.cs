using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.ViewModels.Dialogs;

namespace LightStudio.LightPlayer.Views.Dialogs;

/// <summary>
/// Lets the user search registered lyric sources, pick a candidate to download,
/// import an external LRC file, or clear cached lyrics. Closes with a
/// <see cref="LyricEditResult"/> describing the change, or null when cancelled.
/// </summary>
public partial class LyricSearchDialog : Window
{
    private static readonly FilePickerFileType LrcType = new("Lyrics")
    {
        Patterns = new[] { "*.lrc", "*.txt" },
    };

    private LyricSearchDialogViewModel? viewModel;

    public LyricSearchDialog()
    {
        InitializeComponent();
        SelectButton.Click += OnSelect;
        CancelButton.Click += OnCancel;
        ImportButton.Click += OnImport;
        ClearButton.Click += OnClear;
        CandidateList.DoubleTapped += OnListDoubleTapped;
    }

    public LyricSearchDialog(LyricSearchDialogViewModel viewModel)
        : this()
    {
        this.viewModel = viewModel;
        DataContext = viewModel;
    }

    private async void OnSelect(object? sender, RoutedEventArgs e) => await DownloadAndCloseAsync();

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private async void OnListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (viewModel?.CanSelect == true)
        {
            await DownloadAndCloseAsync();
        }
    }

    private async Task DownloadAndCloseAsync()
    {
        if (viewModel is null || !viewModel.CanSelect)
        {
            return;
        }

        viewModel.IsBusy = true;
        var parsed = await viewModel.DownloadSelectedAsync();
        viewModel.IsBusy = false;
        Close(new LyricEditResult(parsed is not null, parsed));
    }

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (viewModel is null)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import lyrics",
            AllowMultiple = false,
            FileTypeFilter = new[] { LrcType, FilePickerFileTypes.All },
        });

        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var parsed = await viewModel.ImportAsync(path);
        Close(new LyricEditResult(parsed is not null, parsed));
    }

    private async void OnClear(object? sender, RoutedEventArgs e)
    {
        if (viewModel is null)
        {
            return;
        }

        await viewModel.ClearAsync();
        Close(new LyricEditResult(true, null));
    }
}
