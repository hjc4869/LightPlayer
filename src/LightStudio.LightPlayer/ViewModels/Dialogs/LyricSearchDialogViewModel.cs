using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Services;
using LightStudio.MediaLibraryCore.Lyrics;
using LightStudio.MediaLibraryCore.Lyrics.RuntimeApi;

namespace LightStudio.LightPlayer.ViewModels.Dialogs;

/// <summary>
/// Backs the lyric search dialog: lets the user edit the title/artist query,
/// search the registered online sources, and pick a candidate to download.
/// </summary>
public sealed partial class LyricSearchDialogViewModel : ObservableObject
{
    private readonly LyricsService lyrics;

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
        LyricsService lyrics,
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
        Load(initialCandidates);
    }

    public string OriginalTitle { get; }

    public string OriginalArtist { get; }

    public ObservableCollection<ExternalLrcInfo> Candidates { get; } = new();

    public IAsyncRelayCommand SearchCommand { get; }

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
            var results = await lyrics.SearchAsync(Title, Artist);
            Load(results);
        }
        catch (Exception ex)
        {
            ResultText = $"Search failed: {ex.Message}";
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

        return lyrics.DownloadAsync(Candidates[SelectedIndex], OriginalTitle, OriginalArtist);
    }

    /// <summary>Imports an external LRC file for the track.</summary>
    public Task<ParsedLrc?> ImportAsync(string path) =>
        lyrics.ImportAsync(OriginalTitle, OriginalArtist, path);

    /// <summary>Clears cached lyrics for the track.</summary>
    public Task ClearAsync() => lyrics.ClearAsync(OriginalTitle, OriginalArtist);

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
            0 => HasSources ? "No results found." : "No lyric sources installed. Add one in Settings > Extensions.",
            1 => "1 result found.",
            _ => $"{results.Count} results found.",
        };
    }
}
