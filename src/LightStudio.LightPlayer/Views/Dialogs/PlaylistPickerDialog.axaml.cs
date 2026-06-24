using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.Views.Dialogs;

/// <summary>
/// Result of the playlist picker: either an existing playlist was chosen, or the
/// user asked to create a new one.
/// </summary>
public sealed record PlaylistPickResult(PlaylistModel? Existing, bool CreateNew);

/// <summary>
/// Lets the user pick a destination playlist or request a new one. Closes with a
/// <see cref="PlaylistPickResult"/>, or null when cancelled.
/// </summary>
public partial class PlaylistPickerDialog : Window
{
    public PlaylistPickerDialog()
    {
        InitializeComponent();
        ConfirmButton.Click += OnConfirm;
        CancelButton.Click += OnCancel;
        NewPlaylistButton.Click += OnNewPlaylist;
        PlaylistList.DoubleTapped += OnListDoubleTapped;
        PlaylistList.SelectionChanged += OnSelectionChanged;
        UpdateConfirmState();
    }

    public PlaylistPickerDialog(IReadOnlyList<PlaylistModel> playlists)
        : this()
    {
        PlaylistList.ItemsSource = playlists;
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => ConfirmSelection();

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnNewPlaylist(object? sender, RoutedEventArgs e) =>
        Close(new PlaylistPickResult(null, CreateNew: true));

    private void OnListDoubleTapped(object? sender, TappedEventArgs e) => ConfirmSelection();

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateConfirmState();

    private void ConfirmSelection()
    {
        if (PlaylistList.SelectedItem is PlaylistModel selected)
        {
            Close(new PlaylistPickResult(selected, CreateNew: false));
        }
    }

    private void UpdateConfirmState() => ConfirmButton.IsEnabled = PlaylistList.SelectedItem is PlaylistModel;
}
