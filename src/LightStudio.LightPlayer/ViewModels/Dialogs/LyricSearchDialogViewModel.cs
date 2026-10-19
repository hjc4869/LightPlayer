using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.MediaLibraryCore.Lyrics;
using LightStudio.MediaLibraryCore.Lyrics.RuntimeApi;

using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.ViewModels.Dialogs;

/// <summary>
/// Backs the lyric search dialog: lets the user edit the title/artist query,
/// search the registered online sources, and pick a candidate to download.
/// </summary>
public sealed partial class LyricSearchDialogViewModel : ObservableObject, IDisposable
{
    private readonly ILyricsService lyrics;
    private readonly CancellationTokenSource lifetimeCts = new();
    private bool disposed;

    [ObservableProperty]
    private string title;

    [ObservableProperty]
    private string artist;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelect))]
    private bool isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelect))]
    private int selectedIndex = -1;

    [ObservableProperty]
    private string resultText = string.Empty;

    public LyricSearchDialogViewModel(
        ILyricsService lyrics,
        string title,
        string artist,
        IReadOnlyList<ExternalLrcInfo> initialCandidates)
    {
        this.lyrics = lyrics;
        OriginalTitle = title;
        OriginalArtist = artist;
        this.title = title;
        this.artist = artist;
        SearchCommand = new AsyncRelayCommand(SearchAsync);
        SelectCommand = new AsyncRelayCommand(SelectAndCloseAsync);
        ImportCommand = new AsyncRelayCommand(ImportAndCloseAsync);
        ClearCommand = new AsyncRelayCommand(ClearAndCloseAsync);
        CancelCommand = new RelayCommand(CancelAndClose);
        Load(initialCandidates);
    }

    /// <summary>
    /// Raised when the user finishes with the dialog. The argument is the resulting change
    /// (download / import / clear), or null when canceled. The host (desktop window or mobile
    /// overlay) subscribes to dismiss itself with this result.
    /// </summary>
    public event Action<LyricEditResult?>? RequestClose;

    /// <summary>
    /// Picks an external LRC file, returning its text (or null when canceled). Supplied by
    /// the host so file selection uses the correct top level (the dialog window or the shell).
    /// </summary>
    public Func<Task<string?>>? LyricContentPicker { get; set; }

    public string OriginalTitle { get; }

    public string OriginalArtist { get; }

    public ObservableCollection<ExternalLrcInfo> Candidates { get; } = new();

    public IAsyncRelayCommand SearchCommand { get; }

    /// <summary>Downloads the selected candidate and closes with the parsed result.</summary>
    public IAsyncRelayCommand SelectCommand { get; }

    /// <summary>Imports an external LRC file and closes with the parsed result.</summary>
    public IAsyncRelayCommand ImportCommand { get; }

    /// <summary>Clears cached lyrics for the track and closes.</summary>
    public IAsyncRelayCommand ClearCommand { get; }

    /// <summary>Dismisses the dialog without making a change.</summary>
    public IRelayCommand CancelCommand { get; }

    public bool CanSelect => !IsBusy && SelectedIndex >= 0 && SelectedIndex < Candidates.Count;

    public bool HasSources => lyrics.HasSources;

    private async Task SearchAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var results = await lyrics.SearchAsync(Title, Artist, lifetimeCts.Token);
            Load(results);
        }
        catch (OperationCanceledException) when (lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            string message = AppErrorReporter.LogAndGetMessage("Lyrics", "Lyric search failed.", ex);
            ResultText = string.Format(Strings.Resources.LyricSearch_SearchFailedFormat, message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Downloads the currently selected candidate, or null when none is selected.</summary>
    public Task<ParsedLrc?> DownloadSelectedAsync()
    {
        if (SelectedIndex < 0 || SelectedIndex >= Candidates.Count)
        {
            return Task.FromResult<ParsedLrc?>(null);
        }

        return lyrics.DownloadAsync(
            Candidates[SelectedIndex],
            OriginalTitle,
            OriginalArtist,
            lifetimeCts.Token);
    }

    /// <summary>Imports an external LRC file for the track.</summary>
    public Task<ParsedLrc?> ImportAsync(string content) =>
        lyrics.ImportAsync(OriginalTitle, OriginalArtist, content);

    /// <summary>Clears cached lyrics for the track.</summary>
    public Task ClearAsync() => lyrics.ClearAsync(OriginalTitle, OriginalArtist);

    private async Task SelectAndCloseAsync()
    {
        if (!CanSelect)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var parsed = await DownloadSelectedAsync();
            IsBusy = false;
            RequestClose?.Invoke(new LyricEditResult(parsed is not null, parsed));
        }
        catch (OperationCanceledException) when (lifetimeCts.IsCancellationRequested)
        {
            IsBusy = false;
        }
        catch (Exception ex)
        {
            IsBusy = false;
            string message = AppErrorReporter.LogAndGetMessage("Lyrics", "Lyric download failed.", ex);
            ResultText = string.Format(Strings.Resources.LyricSearch_SearchFailedFormat, message);
        }
    }

    private async Task ImportAndCloseAsync()
    {
        if (LyricContentPicker is null)
        {
            return;
        }

        var content = await LyricContentPicker();
        if (string.IsNullOrEmpty(content))
        {
            return;
        }

        try
        {
            var parsed = await ImportAsync(content);
            RequestClose?.Invoke(new LyricEditResult(parsed is not null, parsed));
        }
        catch (Exception ex)
        {
            string message = AppErrorReporter.LogAndGetMessage("Lyrics", "Lyric import failed.", ex);
            ResultText = string.Format(Strings.Resources.LyricSearch_SearchFailedFormat, message);
        }
    }

    private async Task ClearAndCloseAsync()
    {
        try
        {
            await ClearAsync();
        }
        catch (Exception ex)
        {
            string message = AppErrorReporter.LogAndGetMessage("Lyrics", "Clearing cached lyrics failed.", ex);
            ResultText = string.Format(Strings.Resources.LyricSearch_SearchFailedFormat, message);
            return;
        }

        RequestClose?.Invoke(new LyricEditResult(true, null));
    }

    private void Load(IReadOnlyList<ExternalLrcInfo> results)
    {
        SelectedIndex = -1;
        Candidates.Clear();
        foreach (var candidate in results)
        {
            Candidates.Add(candidate);
        }

        ResultText = results.Count switch
        {
            0 => HasSources ? Strings.Resources.LyricSearch_NoResultsFound : Strings.Resources.LyricSearch_NoSourcesInstalled,
            1 => Strings.Resources.LyricSearch_OneResultFound,
            _ => string.Format(Strings.Resources.LyricSearch_ResultsFoundFormat, results.Count),
        };
    }

    private void CancelAndClose()
    {
        Dispose();
        RequestClose?.Invoke(null);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        lifetimeCts.Cancel();
        lifetimeCts.Dispose();
    }
}
