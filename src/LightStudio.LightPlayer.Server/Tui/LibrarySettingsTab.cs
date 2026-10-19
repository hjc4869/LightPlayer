using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace LightStudio.LightPlayer.Server.Tui;

/// <summary>The music and video folders the library is built from.</summary>
internal sealed class LibrarySettingsTab : View, IConfigurationTab
{
    private readonly IApplication app;
    private readonly ObservableCollection<string> musicFolders;
    private readonly ObservableCollection<string> videoFolders;
    private readonly ListView musicList;
    private readonly ListView videoList;
    private readonly CheckBox scanOnStartup;

    public LibrarySettingsTab(IApplication app, ServerConfiguration config)
    {
        this.app = app;
        Title = "_Library";
        Width = Dim.Fill();
        Height = Dim.Fill();

        musicFolders = [.. config.LibraryPaths];
        videoFolders = [.. config.VideoPaths];

        Add(new Label { X = 0, Y = 0, Text = "Music folders" });
        musicList = new ListView { X = 0, Y = 1, Width = Dim.Percent(49), Height = Dim.Fill(5) };
        musicList.SetSource(musicFolders);
        Select(musicList, musicFolders, 0);

        Add(new Label { X = Pos.Percent(51), Y = 0, Text = "Video folders" });
        videoList = new ListView { X = Pos.Percent(51), Y = 1, Width = Dim.Fill(), Height = Dim.Fill(5) };
        videoList.SetSource(videoFolders);
        Select(videoList, videoFolders, 0);

        var addMusic = new Button { X = 0, Y = Pos.AnchorEnd(4), Text = "_Add..." };
        addMusic.Accepting += (_, e) => { e.Handled = true; AddFolder(musicFolders, musicList, "Add a music folder"); };
        var removeMusic = new Button { X = Pos.Right(addMusic) + 1, Y = Pos.AnchorEnd(4), Text = "_Remove" };
        removeMusic.Accepting += (_, e) => { e.Handled = true; RemoveFolder(musicFolders, musicList); };

        var addVideo = new Button { X = Pos.Percent(51), Y = Pos.AnchorEnd(4), Text = "A_dd..." };
        addVideo.Accepting += (_, e) => { e.Handled = true; AddFolder(videoFolders, videoList, "Add a video folder"); };
        var removeVideo = new Button { X = Pos.Right(addVideo) + 1, Y = Pos.AnchorEnd(4), Text = "Remo_ve" };
        removeVideo.Accepting += (_, e) => { e.Handled = true; RemoveFolder(videoFolders, videoList); };

        scanOnStartup = new CheckBox
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Text = "Scan the library every time the server starts",
            Value = config.ScanOnStartup ? CheckState.Checked : CheckState.UnChecked,
        };

        Add(
            musicList,
            videoList,
            addMusic,
            removeMusic,
            addVideo,
            removeVideo,
            new Label { X = 0, Y = Pos.AnchorEnd(2), Text = "A folder has to be reachable from inside the container, so mount it there first." },
            scanOnStartup);
    }

    public string? Apply(ServerConfiguration config)
    {
        config.LibraryPaths = [.. musicFolders];
        config.VideoPaths = [.. videoFolders];
        config.ScanOnStartup = scanOnStartup.IsChecked();
        return null;
    }

    private void AddFolder(ObservableCollection<string> folders, ListView list, string title)
    {
        using var dialog = new OpenDialog
        {
            Title = title,
            OpenMode = OpenMode.Directory,
            AllowsMultipleSelection = false,
            MustExist = false,
            // Browsing starts at the root because a container's mounts sit there, not below /app.
            Path = folders.Count > 0 && Directory.Exists(folders[^1]) ? folders[^1] : RootPath(),
        };

        app.Run(dialog);
        if (dialog.Canceled)
        {
            return;
        }

        IEnumerable<string> picked = dialog.FilePaths.Count > 0
            ? dialog.FilePaths
            : dialog.Path is { Length: > 0 } typed ? [typed] : [];
        foreach (string folder in picked.Select(path => path.TrimEnd(Path.DirectorySeparatorChar)))
        {
            if (folder.Length > 0 && !folders.Contains(folder, StringComparer.Ordinal))
            {
                folders.Add(folder);
            }
        }

        Select(list, folders, folders.Count - 1);
    }

    private static void RemoveFolder(ObservableCollection<string> folders, ListView list)
    {
        if (list.SelectedItem is { } index && index >= 0 && index < folders.Count)
        {
            folders.RemoveAt(index);
            Select(list, folders, index);
        }
    }

    /// <summary>Keeps the highlight on a row that still exists; an empty list has none to offer.</summary>
    private static void Select(ListView list, ObservableCollection<string> folders, int index) =>
        list.SelectedItem = folders.Count > 0 ? Math.Clamp(index, 0, folders.Count - 1) : null;

    private static string RootPath() =>
        Path.GetPathRoot(Directory.GetCurrentDirectory()) is { Length: > 0 } root
            ? root
            : Directory.GetCurrentDirectory();
}
