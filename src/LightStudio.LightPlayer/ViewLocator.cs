using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using LightStudio.LightPlayer.ViewModels;
using LightStudio.LightPlayer.ViewModels.Pages;
using LightStudio.LightPlayer.Views;
using LightStudio.LightPlayer.Views.Pages;

namespace LightStudio.LightPlayer;

public class ViewLocator : IDataTemplate
{
    // Explicit view-model -> view factory map. Instantiating the views directly
    // (instead of reflecting over type names with Type.GetType /
    // Activator.CreateInstance) keeps the locator AOT- and trimming-friendly:
    // the linker sees each view constructor is used and preserves it, and no
    // runtime type metadata is required.
    private static readonly Dictionary<Type, Func<Control>> ViewFactories = new()
    {
        [typeof(ShellViewModel)] = static () => new ShellView(),

        [typeof(HomePageViewModel)] = static () => new HomePageView(),
        [typeof(NowPlayingPageViewModel)] = static () => new NowPlayingPageView(),
        [typeof(AlbumsPageViewModel)] = static () => new GridItemsPageView(),
        [typeof(AlbumDetailPageViewModel)] = static () => new AlbumDetailPageView(),
        [typeof(ArtistsPageViewModel)] = static () => new GridItemsPageView(),
        [typeof(ArtistDetailPageViewModel)] = static () => new ArtistDetailPageView(),
        [typeof(ListItemsPageViewModel)] = static () => new ListItemsPageView(),
        [typeof(SongsPageViewModel)] = static () => new SongsPageView(),
        [typeof(PlaylistsPageViewModel)] = static () => new PlaylistsPageView(),
        [typeof(PlaylistDetailPageViewModel)] = static () => new PlaylistDetailPageView(),
        [typeof(VideoPlaylistDetailPageViewModel)] = static () => new VideoPlaylistDetailPageView(),
        [typeof(SearchPageViewModel)] = static () => new SearchPageView(),
        [typeof(PhotoSearchPageViewModel)] = static () => new PhotoSearchPageView(),
        [typeof(MusicSearchPageViewModel)] = static () => new MusicSearchPageView(),
        [typeof(MediaPageViewModel)] = static () => new MediaPageView(),
        [typeof(VideoPlayerPageViewModel)] = static () => new VideoPlayerPageView(),
        [typeof(PhotoViewerPageViewModel)] = static () => new PhotoViewerPageView(),
        [typeof(PlaceholderPageViewModel)] = static () => new PlaceholderPageView(),
        [typeof(SettingsPageViewModel)] = static () => new SettingsPageView(),

        [typeof(AppearanceSettingsSectionViewModel)] = static () => new AppearanceSettingsSectionView(),
        [typeof(ThemeSettingsSectionViewModel)] = static () => new ThemeSettingsSectionView(),
        [typeof(LibrarySettingsSectionViewModel)] = static () => new LibrarySettingsSectionView(),
        [typeof(PlaybackSettingsSectionViewModel)] = static () => new PlaybackSettingsSectionView(),
        [typeof(SampleRateSettingsSectionViewModel)] = static () => new SampleRateSettingsSectionView(),
        [typeof(AudioSettingsSectionViewModel)] = static () => new AudioSettingsSectionView(),
        [typeof(VideoSettingsSectionViewModel)] = static () => new VideoSettingsSectionView(),
        [typeof(RemoteTranscodeSettingsSectionViewModel)] = static () => new RemoteTranscodeSettingsSectionView(),
        [typeof(ExtensionSettingsSectionViewModel)] = static () => new ExtensionSettingsSectionView(),
        [typeof(StorageSettingsSectionViewModel)] = static () => new StorageSettingsSectionView(),
        [typeof(RemoteApiSettingsSectionViewModel)] = static () => new RemoteApiSettingsSectionView(),
        [typeof(RemoteLibrariesSettingsSectionViewModel)] = static () => new RemoteLibrariesSettingsSectionView(),
        [typeof(RemoteLibraryEditorViewModel)] = static () => new RemoteLibraryEditorView(),
    };

    public Control? Build(object? param)
    {
        if (param is null)
            return null;

        var type = param.GetType();
        if (ViewFactories.TryGetValue(type, out var factory))
        {
            return factory();
        }

        return new TextBlock { Text = "Not Found: " + type.FullName };
    }

    public bool Match(object? data)
    {
        return data is ViewModelBase;
    }
}
