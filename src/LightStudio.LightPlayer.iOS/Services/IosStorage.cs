using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Foundation;
using LightStudio.LightPlayer.Services;
using LightStudio.Logging;
using UIKit;

namespace LightStudio.LightPlayer.iOS.Services;

internal sealed class IosStorage : ILibraryFolderAccess, IFileShareService, IDisposable
{
    private readonly IAppSettingsStore settings;
    private readonly Dictionary<string, NSUrl> scopes = new(StringComparer.Ordinal);
    private readonly string bookmarksPath = Path.Combine(AppPaths.DataDirectory, "FolderBookmarks");
    private readonly string documentsPath = NSFileManager.DefaultManager.GetUrls(
        NSSearchPathDirectory.DocumentDirectory, NSSearchPathDomain.User)[0].Path!;

    public IosStorage(IAppSettingsStore settings)
    {
        this.settings = settings;
        Directory.SetCurrentDirectory(Path.GetDirectoryName(documentsPath)!);
        Directory.CreateDirectory(documentsPath);
        Directory.CreateDirectory(bookmarksPath);
        NormalizeLibraryPaths(this, EventArgs.Empty);
        settings.LibraryScanSettingsChanged += NormalizeLibraryPaths;
        foreach (var bookmarkPath in Directory.EnumerateFiles(bookmarksPath, "*.bookmark"))
        {
            try
            {
                using var data = NSData.FromArray(File.ReadAllBytes(bookmarkPath));
                var url = NSUrl.FromBookmarkData(data, NSUrlBookmarkResolutionOptions.WithoutUI, null,
                    out var stale, out var error);
                if (url?.Path is not { } path || !url.StartAccessingSecurityScopedResource())
                {
                    url?.Dispose();
                    AppLog.Warning("iOS.Storage", $"Could not restore folder access from {bookmarkPath}: {error}");
                    continue;
                }
                scopes.Add(path, url);
                if (stale) SaveBookmark(url, bookmarkPath);
            }
            catch (Exception exception)
            {
                AppLog.Warning("iOS.Storage", $"Could not restore folder bookmark {bookmarkPath}.", exception);
            }
        }
    }

    public Task<LibraryFolderReference?> PersistFolderAccessAsync(IStorageFolder folder)
    {
        var path = folder.TryGetLocalPath();
        if (path is null) return Task.FromResult<LibraryFolderReference?>(null);
        if (!scopes.ContainsKey(path) && !path.StartsWith(documentsPath + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && path != documentsPath)
        {
            var url = NSUrl.FromFilename(path);
            if (!url.StartAccessingSecurityScopedResource())
            {
                url.Dispose();
                return Task.FromResult<LibraryFolderReference?>(null);
            }
            try
            {
                SaveBookmark(url, BookmarkPath(path));
                scopes.Add(path, url);
            }
            catch
            {
                url.StopAccessingSecurityScopedResource();
                url.Dispose();
                throw;
            }
        }
        return Task.FromResult<LibraryFolderReference?>(new(ToIdentifier(path), folder.Name));
    }

    public bool ProvidesFolderPicker => true;

    public async Task<LibraryFolderReference?> PickFolderAsync(bool forVideo)
    {
        var view = (Avalonia.Application.Current?.ApplicationLifetime as ISingleViewApplicationLifetime)?.MainView;
        var topLevel = TopLevel.GetTopLevel(view);
        if (topLevel is null) return null;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = forVideo
                ? Strings.Resources.LibrarySettings_AddVideoFolder
                : Strings.Resources.LibrarySettings_AddMusicFolderPickerTitle,
        });
        if (folders.Count == 0) return null;
        using var folder = folders[0];
        return await PersistFolderAccessAsync(folder);
    }

    public async Task<bool> RequestAccessAsync(string identifier) =>
        await PickFolderAsync(forVideo: false) is { } picked && picked.Identifier == identifier;

    public string? GetDisplayName(string identifier) => Path.GetFileName(identifier.TrimEnd('/'));
    public IReadOnlyList<string> GetDefaultMusicFolderPaths() => ["Documents"];
    public IReadOnlyList<string> GetDefaultVideoFolderPaths() => ["Documents"];
    public bool RequiresAccessGrant(string identifier) => !Directory.Exists(identifier);

    public Task ReleaseAsync(string identifier)
    {
        if (scopes.Remove(identifier, out var url))
        {
            url.StopAccessingSecurityScopedResource();
            url.Dispose();
        }
        File.Delete(BookmarkPath(identifier));
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<string>> PickMediaAsync()
    {
        var view = (Avalonia.Application.Current?.ApplicationLifetime as ISingleViewApplicationLifetime)?.MainView;
        var topLevel = TopLevel.GetTopLevel(view);
        if (topLevel is null) return [];
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true,
            FileTypeFilter = [Views.ShellView.MediaFiles],
        });
        return await ImportMediaAsync(files);
    }

    internal async Task<IReadOnlyList<string>> ImportMediaAsync(IReadOnlyList<IStorageFile> files)
    {
        var paths = new List<string>();
        foreach (var file in files)
        {
            using (file)
            {
                var directory = Path.Combine(documentsPath, "Imports", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, Path.GetFileName(file.Name));
                try
                {
                    await using var source = await file.OpenReadAsync();
                    await using var destination = File.Create(path);
                    await source.CopyToAsync(destination);
                    paths.Add(ToIdentifier(path));
                }
                catch
                {
                    Directory.Delete(directory, recursive: true);
                    throw;
                }
            }
        }
        return paths;
    }

    public bool CanShare => true;
    public Task OpenWithAsync(string filePath, string? mimeType) => ShareAsync(filePath, mimeType);

    public Task ShareAsync(string filePath, string? mimeType)
    {
        var controller = GetViewController();
        var sheet = new UIActivityViewController([NSUrl.FromFilename(Path.GetFullPath(filePath))], null);
        if (sheet.PopoverPresentationController is { } popover)
        {
            popover.SourceView = controller.View!;
            popover.SourceRect = controller.View!.Bounds;
        }
        controller.PresentViewController(sheet, true, null);
        return Task.CompletedTask;
    }

    internal static UIViewController GetViewController()
    {
        foreach (var scene in UIApplication.SharedApplication.ConnectedScenes)
        {
            if (scene is not UIWindowScene windowScene) continue;
            foreach (var window in windowScene.Windows)
            {
                if (!window.IsKeyWindow || window.RootViewController is not { } controller) continue;
                while (controller.PresentedViewController is { } presented) controller = presented;
                return controller;
            }
        }
        throw new InvalidOperationException("The iOS application has no active view controller.");
    }

    public void Dispose()
    {
        settings.LibraryScanSettingsChanged -= NormalizeLibraryPaths;
        foreach (var url in scopes.Values)
        {
            url.StopAccessingSecurityScopedResource();
            url.Dispose();
        }
        scopes.Clear();
    }

    private void NormalizeLibraryPaths(object? sender, EventArgs args)
    {
        var music = settings.LibraryFolderPaths.Select(ToIdentifier).ToArray();
        var video = settings.VideoFolderPaths.Select(ToIdentifier).ToArray();
        if (!music.SequenceEqual(settings.LibraryFolderPaths)) settings.SetLibraryFolderPaths(music);
        if (!video.SequenceEqual(settings.VideoFolderPaths)) settings.SetVideoFolderPaths(video);
    }

    private string BookmarkPath(string path) => Path.Combine(bookmarksPath,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))) + ".bookmark");

    private string ToIdentifier(string path) => path == documentsPath ||
        path.StartsWith(documentsPath + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? Path.GetRelativePath(Path.GetDirectoryName(documentsPath)!, path)
            : path;

    private static void SaveBookmark(NSUrl url, string path)
    {
        using var data = url.CreateBookmarkData(NSUrlBookmarkCreationOptions.MinimalBookmark, null, null, out var error);
        if (data is null) throw new IOException($"Could not save folder access: {error}");
        File.WriteAllBytes(path, data.ToArray());
    }
}