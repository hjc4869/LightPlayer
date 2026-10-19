using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.ViewModels.Dialogs;
using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.Views.Dialogs;

/// <summary>
/// Desktop window host for the lyric search/manage experience. The UI and all actions live in
/// <see cref="LyricSearchView"/> / <see cref="LyricSearchDialogViewModel"/>; this window only
/// supplies the file picker (its own top level) and closes with the view model's result.
/// </summary>
public partial class LyricSearchDialog : Window
{
    private LyricSearchDialogViewModel? viewModel;
    private static readonly FilePickerFileType LrcType = new("Lyrics")
    {
        Patterns = new[] { "*.lrc", "*.txt" },
    };

    public LyricSearchDialog()
    {
        InitializeComponent();
    }

    public LyricSearchDialog(LyricSearchDialogViewModel viewModel)
        : this()
    {
        this.viewModel = viewModel;
        DataContext = viewModel;
        viewModel.LyricContentPicker = PickLyricContentAsync;
        viewModel.RequestClose += OnRequestClose;
    }

    private void OnRequestClose(LyricEditResult? result) => Close(result);

    protected override void OnClosed(EventArgs e)
    {
        if (viewModel is not null)
        {
            viewModel.RequestClose -= OnRequestClose;
            viewModel.Dispose();
            viewModel = null;
        }

        base.OnClosed(e);
    }

    private async Task<string?> PickLyricContentAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Resources.Common_ImportLyrics,
            AllowMultiple = false,
            FileTypeFilter = new[] { LrcType, FilePickerFileTypes.All },
        });

        if (files.Count == 0)
        {
            return null;
        }

        await using var stream = await files[0].OpenReadAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync();
    }
}
