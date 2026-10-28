using LightMediaRenderer.Avalonia.Audio;
using LightStudio.LightPlayer.Video.Pipeline;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Shaders;
using LightMediaRenderer.Subtitles;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Decoding;
using LightStudio.FfmpegShim.Demuxing;
using LightStudio.FfmpegShim.Media;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Services.Playback;
using LightMediaRenderer.Avalonia.Pipeline;
using LightMediaRenderer.Avalonia.Shaders;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.Database.Entities;

using LibrarySourceId = LightStudio.LightPlayer.Models.LibrarySourceId;
using MediaReference = LightStudio.LightPlayer.Models.MediaReference;
using SourcedVideo = LightStudio.LightPlayer.Models.Sourced<LightStudio.MediaLibraryCore.Database.Entities.DbVideoFile>;
using Strings = LightStudio.LightPlayer.Strings;
using SubtitleBrightness = LightMediaRenderer.Contracts.SubtitleBrightnessMode;
using VideoPlaybackRequest = LightStudio.LightPlayer.Models.VideoPlaybackRequest;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>One entry of the playback page's own video list (never the music queue).</summary>
public sealed class VideoListItemViewModel(DbVideoFile video, IRelayCommand<DbVideoFile> playCommand, bool isCurrent)
{
    public DbVideoFile Video { get; } = video;

    /// <summary>Whether this is the entry on screen; the list is rebuilt whenever that changes.</summary>
    public bool IsCurrent { get; } = isCurrent;

    public string Title { get; } = video.DisplayName ?? string.Empty;

    public string Duration { get; } = video.Duration > TimeSpan.Zero
        ? video.Duration.TotalHours >= 1
            ? video.Duration.ToString(@"h\:mm\:ss")
            : video.Duration.ToString(@"m\:ss")
        : string.Empty;

    public IRelayCommand<DbVideoFile> PlayCommand { get; } = playCommand;
}

/// <summary>One entry of the subtitle picker; the entry with no track turns subtitles off.</summary>
public sealed class SubtitleOption(SubtitleTrack? track)
{
    public SubtitleTrack? Track { get; } = track;

    public string DisplayName { get; } = track?.DisplayName ?? Strings.Resources.VideoPlayer_SubtitlesOff;
}

/// <summary>One entry of the plain text subtitle font picker; the entry with no family keeps the generated font.</summary>
public sealed class SubtitleFontOption(string? family)
{
    public string? Family { get; } = family;

    public string DisplayName { get; } = family ?? Strings.Resources.VideoPlayer_SubtitleFontDefault;
}

/// <summary>One way to place subtitle luminance onto an extended-linear output.</summary>
public sealed class SubtitleBrightnessOption(
    SubtitleBrightness mode,
    string resourceKey,
    string fallbackTitle) : ViewModelBase
{
    public SubtitleBrightness Mode { get; } = mode;

    public string DisplayName => Localized(resourceKey, fallbackTitle);
}

/// <summary>One row of the server subtitle browser: a folder to open, or a script to load.</summary>
public sealed class SubtitleBrowserItemViewModel(
    SubtitleFileEntry entry,
    IRelayCommand<SubtitleFileEntry> command)
{
    public SubtitleFileEntry Entry { get; } = entry;

    public string Name { get; } = entry.Name;

    public bool IsFolder { get; } = entry.IsFolder;

    public IRelayCommand<SubtitleFileEntry> Command { get; } = command;
}

/// <summary>One step of the server subtitle browser's path; the first has no folder of its own.</summary>
public sealed class SubtitleBrowserCrumbViewModel(
    string name,
    string? path,
    IRelayCommand<string?> command)
{
    public string Name { get; } = name;

    public string? Path { get; } = path;

    public IRelayCommand<string?> Command { get; } = command;
}

/// <summary>One entry of the playback speed picker.</summary>
public sealed class SpeedOption(double value)
{
    public double Value { get; } = value;

    public string DisplayName { get; } = value.ToString("0.##", CultureInfo.CurrentCulture) + "×";
}

/// <summary>Automatic, off, or one installed package in the video settings flyout.</summary>
public sealed class VideoShaderPackageOption(string? value, string title)
{
    public string? Value { get; } = value;

    public string Title { get; } = title;
}

/// <summary>One resolution and bandwidth represented by one or more camera renditions.</summary>
public sealed class VideoQualityOption(IReadOnlyList<VideoRendition> renditions)
{
    internal IReadOnlyList<VideoRendition> Renditions { get; } = renditions;

    public string DisplayName { get; } = BuildDisplayName(renditions[0]);

    private static string BuildDisplayName(VideoRendition rendition)
    {
        string resolution = rendition.Width > 0 && rendition.Height > 0
            ? $"{rendition.Width} x {rendition.Height}"
            : $"#{rendition.StreamIndex}";
        return rendition.Bandwidth > 0
            ? $"{resolution} ({rendition.Bandwidth / 1_000_000d:0.##} Mbps)"
            : resolution;
    }
}

/// <summary>One named HLS alternate video rendition.</summary>
public sealed class VideoAngleOption(string name)
{
    public string Name { get; } = name;
}

/// <summary>
/// The dedicated video playback page. Owns its own <see cref="MediaPlayer"/> and its own list of
/// videos, so nothing here ever reaches the music queue.
/// </summary>
public sealed partial class VideoPlayerPageViewModel : PageViewModelBase, INavigationParameterSource, IDisposable, IAsyncDisposable
{
    /// <summary>Values of the re-encoding menu, which is a three-way choice rather than a switch.</summary>
    private const string TranscodeOn = "on";
    private const string TranscodeOff = "off";

    /// <summary>Multiple of the chosen rate the picture runs at while a finger is held on it.</summary>
    private const double HoldSpeedMultiplier = 2.0;

    internal const SubtitleBrightness DefaultSubtitleBrightnessMode = SubtitleBrightness.FollowVideo;

    /// <summary>
    /// Label of the subtitle last chosen in this session. Held for the lifetime of the process (no
    /// setting is written) so a run of episodes keeps the same track without asking each time.
    /// </summary>
    private static string? preferredSubtitleName;

    private readonly IAppSettingsStore settingsStore;
    private readonly PlaybackController playbackController;
    private readonly IVideoPlaybackStateStore playbackStates;
    private readonly bool persistSubtitleImports;
    private readonly IDialogService? dialogService;
    private readonly IClientMediaResolver mediaResolver;
    private readonly MediaPlayerOptions playerOptions;
    private readonly VideoShaderPackageCatalog? shaderPackages;
    private readonly SemaphoreSlim openSync = new(1, 1);
    private ISubtitleFileBrowser? subtitleFiles;
    private readonly Action<bool> setFullScreen;
    private readonly Action<double> setPreferredOrientation;
    private readonly Action<bool> setKeepScreenOn;

    /// <summary>
    /// How each hand-picked subtitle of the video on screen is fetched again, keyed by the file it
    /// came from. Sidecars found by the indexer are absent, which is what makes them
    /// <see cref="SubtitleSourceKind.Indexed"/>.
    /// </summary>
    private readonly Dictionary<string, SubtitleSourceKind> subtitleSourceKinds = new(StringComparer.Ordinal);

    private SubtitleTrack? hiddenSubtitleTrack;

    /// <summary>Where the volume goes back to when the mute is lifted.</summary>
    private double volumeBeforeMute = 0.8;

    private DbVideoFile? currentVideo;
    private LibrarySourceId currentSourceId = LibrarySourceId.Local;
    private bool persistPlaybackState = true;
    private SystemMediaSession? mediaSession;
    private bool suppressTrackSync;
    private bool restoringTracks;
    private bool restoringSubtitleBrightness;
    private bool isTranscodeLoaded;
    private RemoteTranscodeOverride transcodeOverride = RemoteTranscodeOverride.None;
    private bool isShaderPackageLoaded;
    private bool isVideoRenditionLoaded;
    private bool subtitleFontsRequested;
    private IStreamingMediaSourceProvider? currentStreamProvider;
    private Uri? currentStreamUri;
    private int streamOpenInProgress;
    private bool disposed;

    [ObservableProperty]
    private string mediaTitle = string.Empty;

    /// <summary>Reopens the page on the video it is playing now; see <see cref="INavigationParameterSource"/>.</summary>
    [ObservableProperty]
    private object? navigationParameter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    [NotifyPropertyChangedFor(nameof(TimeText))]
    [NotifyPropertyChangedFor(nameof(IsAtLiveEdge))]
    private double positionSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    [NotifyPropertyChangedFor(nameof(TimeText))]
    [NotifyPropertyChangedFor(nameof(CanSeek))]
    [NotifyPropertyChangedFor(nameof(SeekMaximumSeconds))]
    private double durationSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSeek))]
    [NotifyPropertyChangedFor(nameof(SeekMinimumSeconds))]
    [NotifyPropertyChangedFor(nameof(SeekMaximumSeconds))]
    [NotifyPropertyChangedFor(nameof(IsAtLiveEdge))]
    [NotifyPropertyChangedFor(nameof(TimeText))]
    [NotifyPropertyChangedFor(nameof(IsPlaylistAvailable))]
    private bool isLiveStream;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SeekMinimumSeconds))]
    [NotifyPropertyChangedFor(nameof(IsAtLiveEdge))]
    private double liveWindowStartSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SeekMaximumSeconds))]
    [NotifyPropertyChangedFor(nameof(IsAtLiveEdge))]
    [NotifyPropertyChangedFor(nameof(TimeText))]
    private double liveWindowEndSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAtLiveEdge))]
    private double liveTargetDurationSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSeek))]
    private bool canSeekBackWithinLiveWindow;

    /// <summary>How far a network source has fetched ahead; 0 for one that reads on demand.</summary>
    [ObservableProperty]
    private double bufferedSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseIconKey))]
    private bool isPlaying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlaybackBusy))]
    private bool isBuffering;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlaybackBusy))]
    private bool isStreamLoading;

    [ObservableProperty]
    private string streamLoadingText = string.Empty;

    [ObservableProperty]
    private string? errorMessage;

    public double Volume
    {
        get => playbackController.Volume;
        set => playbackController.SetVolume(value);
    }

    [ObservableProperty]
    private double speed = 1.0;

    /// <summary>Whether the picture is running fast because a finger is being held on it.</summary>
    [ObservableProperty]
    private bool isSpeedBoosted;

    [ObservableProperty]
    private bool isFullScreen;

    /// <summary>Whether the picture is being shown outside the app's own window.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPictureDetached))]
    private bool isPictureInPicture;

    /// <summary>Whether reaching the end of a video opens the next entry of the list by itself.</summary>
    [ObservableProperty]
    private bool isAutoPlayNextEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AreDeckControlsVisible))]
    private bool areControlsVisible = true;

    /// <summary>
    /// Whether the picture is showing in the sheet's small corner card rather than filling the
    /// shell. Everything drawn over the picture is unreadable at that size, so the deck and the
    /// panels step aside and the card's own overlay takes their place.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AreDeckControlsVisible))]
    [NotifyPropertyChangedFor(nameof(IsStatisticsPanelVisible))]
    private bool isMinimized;

    [ObservableProperty]
    private bool isPlaylistOpen;

    /// <summary>Whether the server's subtitle browser is covering the picture.</summary>
    [ObservableProperty]
    private bool isSubtitleBrowserOpen;

    [ObservableProperty]
    private bool isSubtitleBrowserLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSubtitleBrowserEmpty))]
    private IReadOnlyList<SubtitleBrowserItemViewModel> subtitleBrowserItems = [];

    [ObservableProperty]
    private IReadOnlyList<SubtitleBrowserCrumbViewModel> subtitleBrowserPath = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatisticsPanelVisible))]
#if DEBUG
    private bool areStatisticsVisible = true;
#else
    private bool areStatisticsVisible;
#endif

    /// <summary>Whether the list of keyboard shortcuts is covering the picture.</summary>
    [ObservableProperty]
    private bool areShortcutsVisible;

    /// <summary>Formatted pipeline and decoder diagnostics, refreshed only while they are on show.</summary>
    [ObservableProperty]
    private string statisticsText = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<AudioTrack> audioTracks = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVideoQualityChoices))]
    private IReadOnlyList<VideoQualityOption> videoQualityOptions = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCameraAngleChoices))]
    private IReadOnlyList<VideoAngleOption> cameraAngleOptions = [];

    [ObservableProperty]
    private VideoQualityOption? selectedVideoQualityOption;

    [ObservableProperty]
    private VideoAngleOption? selectedCameraAngleOption;

    [ObservableProperty]
    private IReadOnlyList<SubtitleTrack> subtitleTracks = [];

    /// <summary>The subtitle picker's entries: every track, behind an entry that turns them off.</summary>
    [ObservableProperty]
    private IReadOnlyList<SubtitleOption> subtitleOptions = [];

    [ObservableProperty]
    private IReadOnlyList<MediaChapter> chapters = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlaylistAvailable))]
    private IReadOnlyList<VideoListItemViewModel> playlist = [];

    [ObservableProperty]
    private AudioTrack? selectedAudioTrack;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSetSubtitleFont))]
    private SubtitleTrack? selectedSubtitleTrack;

    [ObservableProperty]
    private SubtitleOption? selectedSubtitleOption;

    /// <summary>Font size of plain text subtitles, in script pixels of a 288-line frame.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlainTextSubtitleFontSize))]
    private decimal subtitleFontSize;

    /// <summary>Installed families behind an entry that keeps the generated font; filled on first use.</summary>
    [ObservableProperty]
    private IReadOnlyList<SubtitleFontOption> subtitleFontOptions;

    [ObservableProperty]
    private SubtitleFontOption? selectedSubtitleFontOption;

    /// <summary>Font family of plain text subtitles, or null for the generated font.</summary>
    [ObservableProperty]
    private string? plainTextSubtitleFontFamily;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubtitleBrightnessMode))]
    private SubtitleBrightnessOption selectedSubtitleBrightnessOption = null!;

    [ObservableProperty]
    private bool isExtendedRangeOutput;

    [ObservableProperty]
    private MediaChapter? selectedChapter;

    [ObservableProperty]
    private Stretch stretch = Stretch.Uniform;

    [ObservableProperty]
    private VideoScalingQuality scalingQuality;

    [ObservableProperty]
    private VideoScalingQuality downscalingQuality;

    [ObservableProperty]
    private VideoDebandingQuality debandingQuality;

    [ObservableProperty]
    private bool preferHardwareProcessing;

    [ObservableProperty]
    private string? shaderPackageOverride;

    [ObservableProperty]
    private VideoShaderPackageOption selectedShaderPackage = null!;

    [ObservableProperty]
    private bool enableHdrPassthrough;

    [ObservableProperty]
    private int displayPanelBitDepth;

    /// <summary>The active tone mapping options, or null for the built-in mapping.</summary>
    [ObservableProperty]
    private VideoSdrToneMappingOptions? toneMapping;

    public PhotoToneMappingViewModel ToneProfiles { get; }

    /// <summary>Width over height of the picture, or 0 before anything is known about it.</summary>
    [ObservableProperty]
    private double pictureAspectRatio;

    [ObservableProperty]
    private SpeedOption? selectedSpeedOption;

    [ObservableProperty]
    private VideoSettingsOption selectedVideoTranscode = null!;

    [ObservableProperty]
    private VideoSettingsOption selectedAudioTranscode = null!;

    [ObservableProperty]
    private decimal transcodeVideoBitrate;

    [ObservableProperty]
    private decimal? transcodeVideoHeight;

    [ObservableProperty]
    private decimal transcodeAudioBitrate;

    /// <summary>Whether the menu holds terms the server has not been asked on yet.</summary>
    [ObservableProperty]
    private bool hasPendingTranscodeChanges;

    /// <summary>Whether the source the video on screen came from offers to re-encode.</summary>
    [ObservableProperty]
    private bool isTranscodeOffered;

    /// <param name="setFullScreen">Asks the host window to enter or leave platform full screen.</param>
    /// <param name="setPreferredOrientation">
    /// Requests a screen orientation for the given picture aspect ratio; ignored on platforms that
    /// do not rotate (a value of 0 releases any earlier request).
    /// </param>
    /// <param name="setKeepScreenOn">
    /// Holds the display awake while the picture is running. Driven by playback rather than by the
    /// page being open, so a video left paused does not keep the screen lit.
    /// </param>
    /// <param name="playbackStates">Holds each video's resume point and track choices.</param>
    /// <param name="dialogService">Asks whether to resume; without one a video always starts over.</param>
    /// <param name="subtitleFiles">
    /// Browses subtitles where the media lives. Non-null only when that is a server, which is what
    /// puts the choice between this device and the server in front of the file picker.
    /// </param>
    /// <param name="minimize">Drops the sheet back to its corner card without ending playback.</param>
    /// <param name="close">Ends the video session and takes the sheet away altogether.</param>
    public VideoPlayerPageViewModel(
        IAppSettingsStore settingsStore,
        PlaybackController playbackController,
        IVideoPlaybackStateStore playbackStates,
        IDialogService? dialogService,
        ISubtitleFileBrowser? subtitleFiles,
        IClientMediaResolver mediaResolver,
        Action<bool> setFullScreen,
        Action<double> setPreferredOrientation,
        Action<bool> setKeepScreenOn,
        Action? minimize,
        Action close,
        bool persistSubtitleImports = true)
    {
        this.settingsStore = settingsStore;
        this.playbackController = playbackController;
        this.playbackStates = playbackStates;
        this.persistSubtitleImports = persistSubtitleImports;
        this.dialogService = dialogService;
        this.subtitleFiles = subtitleFiles;
        this.mediaResolver = mediaResolver;
        shaderPackages = PlatformServices.VideoShaderPackages;
        this.setFullScreen = setFullScreen;
        this.setPreferredOrientation = setPreferredOrientation;
        this.setKeepScreenOn = setKeepScreenOn;
        CanMinimize = minimize is not null;
        TitleKey = nameof(Strings.Resources.Nav_VideoPlayer);

        scalingQuality = settingsStore.PreferredVideoScaling;
        downscalingQuality = settingsStore.PreferredVideoDownscaling;
        debandingQuality = settingsStore.VideoDebanding;
        preferHardwareProcessing = settingsStore.PreferHardwareVideoProcessing;
        enableHdrPassthrough = settingsStore.EnableHdrPassthrough;
        VideoSurfaceTransfers.Bt709Preference = settingsStore.VideoBt709Interpretation;
        displayPanelBitDepth = settingsStore.DisplayPanelBitDepth;
        ToneProfiles = new PhotoToneMappingViewModel(settingsStore, forVideo: true);
        toneMapping = ToneProfiles.Options;
        ToneProfiles.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(PhotoToneMappingViewModel.Options))
                ToneMapping = ToneProfiles.Options;
        };
        isAutoPlayNextEnabled = settingsStore.AutoPlayNextVideo;
        subtitleFontSize = settingsStore.SubtitleFontSize;
        string savedFontFamily = settingsStore.SubtitleFontFamily;
        plainTextSubtitleFontFamily = savedFontFamily.Length > 0 ? savedFontFamily : null;
        subtitleFontOptions = plainTextSubtitleFontFamily is null
            ? [new SubtitleFontOption(null)]
            : [new SubtitleFontOption(null), new SubtitleFontOption(plainTextSubtitleFontFamily)];
        selectedSubtitleFontOption = subtitleFontOptions[^1];
        Player = new MediaPlayer(playerOptions = new MediaPlayerOptions
        {
            SourceProviderFactory = MediaSourceProviders.Create,
            OpenFile = MediaSourceProviders.OpenFile,
            VideoDecoderOptionsProvider = () => VideoDecoderOptions.Default with
            {
                EnableHardwareDecoding = settingsStore.EnableHardwareDecoding,
                HardwareDeviceType = string.IsNullOrWhiteSpace(settingsStore.PreferredHardwareDecoder)
                    ? null : settingsStore.PreferredHardwareDecoder,
                HardwareSettings = HardwareDecoderProfiles.Compose(settingsStore),
            },
            VideoBufferDurationProvider = () => settingsStore.VideoBufferDuration,
            AudioSampleRateProvider = () => !settingsStore.AlwaysResample ? 0
                : settingsStore.PreferredSampleRate > 0 ? settingsStore.PreferredSampleRate
                : AudioOutputDeviceFactory.SystemSampleRateProvider.GetSystemSampleRate(),
            DefaultTrackNameProvider = () => Strings.Resources.VideoPlayer_TrackDefault,
        }) { Volume = (float)Volume };
        playbackController.PropertyChanged += OnPlaybackControllerPropertyChanged;
        Player.StateChanged += OnStateChanged;
        Player.StallChanged += OnStallChanged;
        Player.ErrorOccurred += OnErrorOccurred;
        Player.EndReached += OnEndReached;
        Player.AudioTracksChanged += OnAudioTracksChanged;
        Player.AudioTrackChanged += OnAudioTrackChanged;
        Player.VideoRenditionsChanged += OnVideoRenditionsChanged;
        Player.VideoRenditionChanged += OnVideoRenditionChanged;
        Player.SubtitleTracksChanged += OnSubtitleTracksChanged;
        Player.SubtitleTrackChanged += OnSubtitleTrackChanged;
        Player.ChaptersChanged += OnChaptersChanged;

        PlayPauseCommand = new RelayCommand(() => Player.TogglePause());
        StopCommand = new RelayCommand(Stop);
        SkipBackCommand = new RelayCommand(() => SeekRelative(-10));
        SkipForwardCommand = new RelayCommand(() => SeekRelative(10));
        PreviousChapterCommand = new RelayCommand(() => StepChapter(-1), () => Chapters.Count > 1);
        NextChapterCommand = new RelayCommand(() => StepChapter(1), () => Chapters.Count > 1);
        PreviousVideoCommand = new RelayCommand(() => StepPlaylist(-1));
        NextVideoCommand = new RelayCommand(() => StepPlaylist(1));
        GoLiveCommand = new AsyncRelayCommand(GoLiveAsync);
        ToggleFullScreenCommand = new RelayCommand(() => IsFullScreen = !IsFullScreen);
        TogglePictureInPictureCommand = new RelayCommand(
            () => IsPictureInPicture = !IsPictureInPicture,
            () => CanUsePictureInPicture);
        ToggleSubtitlesCommand = new RelayCommand(ToggleSubtitles);
        CycleSubtitleTrackCommand = new RelayCommand(CycleSubtitleTrack, () => SubtitleTracks.Count > 0);
        CycleAudioTrackCommand = new RelayCommand(CycleAudioTrack, () => AudioTracks.Count > 1);
        ToggleMuteCommand = new RelayCommand(ToggleMute);
        GoToLocationCommand = new RelayCommand(() => GoToLocation?.Invoke(new(currentSourceId, currentVideo!)),
            () => CanGoToLocation);
        OpenContainingFolderCommand = new RelayCommand(() => OpenContainingFolder?.Invoke(new(currentSourceId, currentVideo!)),
            () => CanOpenContainingFolder);
        AddToPlaylistCommand = new RelayCommand(() => AddToPlaylist?.Invoke(new(currentSourceId, currentVideo!)),
            () => CanAddToPlaylist);
        PropertiesCommand = new RelayCommand(() => ShowProperties?.Invoke(new(currentSourceId, currentVideo!)),
            () => CanShowProperties);
        TogglePlaylistCommand = new RelayCommand(() =>
        {
            IsSubtitleBrowserOpen = false;
            IsPlaylistOpen = !IsPlaylistOpen;
        });
        ToggleShortcutsCommand = new RelayCommand(() => AreShortcutsVisible = !AreShortcutsVisible);
        PlayVideoCommand = new RelayCommand<DbVideoFile>(video =>
        {
            if (video is not null)
            {
                _ = OpenAsync(
                    new SourcedVideo(currentSourceId, video),
                    Playlist.Select(entry => new SourcedVideo(currentSourceId, entry.Video)).ToList(),
                    resume: false,
                    persistPlaybackState: persistPlaybackState);
            }
        });
        DismissErrorCommand = new RelayCommand(() => ErrorMessage = null);
        ApplyTranscodeCommand = new RelayCommand(ApplyTranscodeOverride, () => HasPendingTranscodeChanges);
        AddLocalSubtitleCommand = new AsyncRelayCommand(AddLocalSubtitleAsync);
        OpenSubtitleBrowserCommand = new AsyncRelayCommand(OpenSubtitleBrowserAsync);
        CloseSubtitleBrowserCommand = new RelayCommand(() => IsSubtitleBrowserOpen = false);
        BrowseSubtitleFolderCommand = new AsyncRelayCommand<string?>(LoadSubtitleFolderAsync);
        ChooseSubtitleEntryCommand = new AsyncRelayCommand<SubtitleFileEntry>(ChooseSubtitleEntryAsync);
        MinimizeCommand = new RelayCommand(() =>
        {
            IsFullScreen = false;
            minimize?.Invoke();
        }, () => CanMinimize);
        CloseCommand = new RelayCommand(() =>
        {
            IsFullScreen = false;
            close();
        });

        SpeedOptions = [new(0.5), new(0.75), new(1.0), new(1.25), new(1.5), new(2.0)];
        selectedSpeedOption = SpeedOptions.First(option => Math.Abs(option.Value - Speed) < 0.01);

        SubtitleBrightnessOptions =
        [
            new(SubtitleBrightness.Sdr, "VideoPlayer_SubtitleBrightnessSdr", "SDR brightness"),
            new(SubtitleBrightness.FollowVideo, "VideoPlayer_SubtitleBrightnessFollowVideo", "Follow video"),
        ];
        selectedSubtitleBrightnessOption = SubtitleBrightnessOptions.First(
            option => option.Mode == DefaultSubtitleBrightnessMode);

        RebuildShaderPackageOptions(video: null, selectedOverride: null);
        if (shaderPackages is not null)
            shaderPackages.Changed += OnShaderPackagesChanged;

        VideoTranscodeOptions =
        [
            new(string.Empty, string.Empty, nameof(Strings.Resources.VideoPlayer_TranscodeFollow)),
            new(TranscodeOn, string.Empty, nameof(Strings.Resources.VideoPlayer_TranscodeVideoOn)),
            new(TranscodeOff, string.Empty, nameof(Strings.Resources.VideoPlayer_TranscodeVideoOff)),
        ];
        AudioTranscodeOptions =
        [
            new(string.Empty, string.Empty, nameof(Strings.Resources.VideoPlayer_TranscodeFollow)),
            new(TranscodeOn, string.Empty, nameof(Strings.Resources.VideoPlayer_TranscodeAudioOn)),
            new(TranscodeOff, string.Empty, nameof(Strings.Resources.VideoPlayer_TranscodeAudioOff)),
        ];

        // The menu starts on "follow the settings": an override belongs to a video, and none is open.
        selectedVideoTranscode = FindTranscodeOption(VideoTranscodeOptions, null);
        selectedAudioTranscode = FindTranscodeOption(AudioTranscodeOptions, null);
        isTranscodeLoaded = true;
        settingsStore.RemoteTranscodeSettingsChanged += OnTranscodeChanged;

        // The window can also be closed from its own frame, which the page has to hear about.
        if (PlatformServices.PictureInPicture is { } pictureInPicture)
        {
            pictureInPicture.ActiveChanged += OnPictureInPictureHostChanged;
        }
    }

    public IReadOnlyList<SpeedOption> SpeedOptions { get; }

    public IReadOnlyList<SubtitleBrightnessOption> SubtitleBrightnessOptions { get; }

    public IReadOnlyList<VideoSettingsOption> VideoTranscodeOptions { get; }

    public IReadOnlyList<VideoSettingsOption> AudioTranscodeOptions { get; }

    public IReadOnlyList<VideoShaderPackageOption> ShaderPackageOptions { get; private set; } = [];

    public bool HasShaderPackageChoices => ShaderPackageOptions.Count > 2;

    public string ShaderPackageHeader =>
        Localized("VideoPlayer_ScalingFilter", "Custom scaling filter");

    public string SubtitleBrightnessHeader =>
        Localized("VideoPlayer_SubtitleBrightness", "Subtitle brightness");

    public SubtitleBrightness SubtitleBrightnessMode =>
        SelectedSubtitleBrightnessOption?.Mode ?? DefaultSubtitleBrightnessMode;

    public double PlainTextSubtitleFontSize => (double)SubtitleFontSize;

    /// <summary>Whether the subtitle on screen has no authored styles, so its font is the player's.</summary>
    public bool CanSetSubtitleFont => SelectedSubtitleTrack?.IsPlainText == true;

    /// <summary>The engine the view's <c>VideoView</c> renders.</summary>
    public MediaPlayer Player { get; }

    public IRelayCommand PlayPauseCommand { get; }

    public IRelayCommand StopCommand { get; }

    public IRelayCommand SkipBackCommand { get; }

    public IRelayCommand SkipForwardCommand { get; }

    public IRelayCommand PreviousChapterCommand { get; }

    public IRelayCommand NextChapterCommand { get; }

    /// <summary>Opens the entry before this one in the page's own video list.</summary>
    public IRelayCommand PreviousVideoCommand { get; }

    public IRelayCommand NextVideoCommand { get; }

    public IAsyncRelayCommand GoLiveCommand { get; }

    public IRelayCommand ToggleFullScreenCommand { get; }

    /// <summary>Detaches the picture into the host's own window, or brings it back.</summary>
    public IRelayCommand TogglePictureInPictureCommand { get; }

    /// <summary>Whether this platform can show the picture outside the app at all.</summary>
    public bool CanUsePictureInPicture => PlatformServices.PictureInPicture is { IsAvailable: true };

    /// <summary>
    /// Whether the picture left this page for a window of its own, which is when the page has
    /// nothing to draw. A host that shrinks the app instead is still showing this very page.
    /// </summary>
    public bool IsPictureDetached =>
        IsPictureInPicture && PlatformServices.PictureInPicture?.Kind == PictureInPictureKind.DetachedView;

    public IRelayCommand ToggleSubtitlesCommand { get; }

    /// <summary>Steps to the next subtitle track, counting "off" as one of them.</summary>
    public IRelayCommand CycleSubtitleTrackCommand { get; }

    public IRelayCommand CycleAudioTrackCommand { get; }

    public IRelayCommand ToggleMuteCommand { get; }

    public Action<SourcedVideo>? GoToLocation { get; init; }

    public Action<SourcedVideo>? OpenContainingFolder { get; init; }

    public Action<SourcedVideo>? AddToPlaylist { get; init; }

    public Action<SourcedVideo>? ShowProperties { get; init; }

    public IRelayCommand GoToLocationCommand { get; }

    public IRelayCommand OpenContainingFolderCommand { get; }

    public IRelayCommand AddToPlaylistCommand { get; }

    public IRelayCommand PropertiesCommand { get; }

    public bool CanGoToLocation => currentVideo is not null && GoToLocation is not null;

    public bool CanOpenContainingFolder => currentVideo is not null && OpenContainingFolder is not null &&
        currentSourceId.IsLocal && PlatformServices.SupportsLocalFileAccess &&
        mediaResolver.GetCapabilities(currentSourceId).SupportsFileReveal;

    public bool CanAddToPlaylist => currentVideo is not null && AddToPlaylist is not null;

    public bool CanShowProperties => currentVideo is not null && ShowProperties is not null;

    public bool HasLocationActions => CanGoToLocation || CanOpenContainingFolder;

    public bool HasFileActions => CanAddToPlaylist || CanShowProperties;

    private void RefreshFileActions()
    {
        OnPropertyChanged(nameof(CanGoToLocation));
        OnPropertyChanged(nameof(CanOpenContainingFolder));
        OnPropertyChanged(nameof(CanAddToPlaylist));
        OnPropertyChanged(nameof(CanShowProperties));
        OnPropertyChanged(nameof(HasLocationActions));
        OnPropertyChanged(nameof(HasFileActions));
        GoToLocationCommand.NotifyCanExecuteChanged();
        OpenContainingFolderCommand.NotifyCanExecuteChanged();
        AddToPlaylistCommand.NotifyCanExecuteChanged();
        PropertiesCommand.NotifyCanExecuteChanged();
    }

    public IRelayCommand TogglePlaylistCommand { get; }

    /// <summary>Whether the frame on screen can be saved as an image file on this platform.</summary>
    public bool CanSaveScreenshot => PlatformServices.VideoScreenshotEncoder is not null;

    public string ScreenshotShortcutText => OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() ? "\u2318 + S" : "Ctrl + S";

    public IRelayCommand ToggleShortcutsCommand { get; }

    public IRelayCommand<DbVideoFile> PlayVideoCommand { get; }

    public IRelayCommand DismissErrorCommand { get; }

    /// <summary>Sends the menu's terms to the server, which reopens the stream.</summary>
    public IRelayCommand ApplyTranscodeCommand { get; }

    /// <summary>Picks a subtitle file off this device and shows it.</summary>
    public IAsyncRelayCommand AddLocalSubtitleCommand { get; }

    /// <summary>Opens the server's subtitle browser at the folder the video is in.</summary>
    public IAsyncRelayCommand OpenSubtitleBrowserCommand { get; }

    public IRelayCommand CloseSubtitleBrowserCommand { get; }

    public IAsyncRelayCommand<string?> BrowseSubtitleFolderCommand { get; }

    public IAsyncRelayCommand<SubtitleFileEntry> ChooseSubtitleEntryCommand { get; }

    /// <summary>Whether the media lives on a server, which is the only place there is to browse.</summary>
    public bool CanBrowseServerSubtitles => subtitleFiles is not null;

    public bool IsSubtitleBrowserEmpty => SubtitleBrowserItems.Count == 0;

    public bool CanMinimize { get; }

    /// <summary>Drops the sheet to its corner card; the deck's own "down" arrow.</summary>
    public IRelayCommand MinimizeCommand { get; }

    /// <summary>Ends playback and takes the sheet away; the deck's own close button.</summary>
    public IRelayCommand CloseCommand { get; }

    public bool CanSeek => IsLiveStream
        ? CanSeekBackWithinLiveWindow && Player.CanSeek && SeekMaximumSeconds > SeekMinimumSeconds
        : DurationSeconds > 0;

    public double SeekMinimumSeconds => IsLiveStream ? LiveWindowStartSeconds : 0;

    public double SeekMaximumSeconds => IsLiveStream ? LiveWindowEndSeconds : DurationSeconds;

    public bool IsAtLiveEdge => IsLiveStream &&
        PositionSeconds >= LiveWindowEndSeconds - Math.Max(1, LiveTargetDurationSeconds * 1.5);

    /// <summary>Whether the deck and the title bar are drawn over the picture.</summary>
    public bool AreDeckControlsVisible => AreControlsVisible && !IsMinimized;

    /// <summary>Diagnostics are unreadable on the corner card, so they wait for the full picture.</summary>
    public bool IsStatisticsPanelVisible => AreStatisticsVisible && !IsMinimized;

    public bool HasSubtitles => SubtitleTracks.Count > 0;

    public bool HasChapters => Chapters.Count > 1;

    public bool HasMultipleAudioTracks => AudioTracks.Count > 1;

    public bool HasVideoQualityChoices => VideoQualityOptions.Count > 1;

    public bool HasCameraAngleChoices => CameraAngleOptions.Count > 1;

    public bool IsPlaylistAvailable => Playlist.Count > 1 && !IsLiveStream;

    public bool IsMuted => Volume <= 0.0001d;

    public bool IsPlaybackBusy => IsBuffering || IsStreamLoading;

    public string PlayPauseIconKey => IsPlaying ? "IconPause" : "IconPlay";

    public string VolumeIconKey => IsMuted ? "IconVolumeMute" : "IconVolumeHigh";

    public string PositionText => Format(TimeSpan.FromSeconds(PositionSeconds));

    public string DurationText => Format(TimeSpan.FromSeconds(DurationSeconds));

    /// <summary>Elapsed and total time as one label, so the deck holds a single fixed-width field.</summary>
    public string TimeText => IsLiveStream
        ? IsAtLiveEdge
            ? string.Empty
            : $"-{Format(TimeSpan.FromSeconds(Math.Max(0, LiveWindowEndSeconds - PositionSeconds)))}"
        : $"{PositionText} / {DurationText}";

    /// <summary>Opens a video together with the list it was picked from.</summary>
    public async Task OpenAsync(
        SourcedVideo sourcedVideo,
        IReadOnlyList<SourcedVideo> siblings,
        bool resume,
        bool persistPlaybackState = true)
    {
        await openSync.WaitAsync().ConfigureAwait(true);
        try
        {
            await OpenCoreAsync(sourcedVideo, siblings, resume, persistPlaybackState).ConfigureAwait(true);
        }
        finally
        {
            openSync.Release();
        }
    }

    /// <summary>Opens one HTTP media URL without attaching it to library history or saved playback state.</summary>
    public Task OpenStreamAsync(Uri streamUri)
    {
        ArgumentNullException.ThrowIfNull(streamUri);
        return OpenStreamAsync(new StreamLink([streamUri]));
    }

    /// <summary>
    /// Opens adaptive media or ordinary HTTP video and audio URLs without adding library history.
    /// </summary>
    public async Task OpenStreamAsync(StreamLink streamLink)
    {
        ArgumentNullException.ThrowIfNull(streamLink);
        if (streamLink.Sources.Count == 0)
            throw new ArgumentException("At least one HTTP media URL is required.", nameof(streamLink));

        Uri streamUri = streamLink.Sources[0];
        await openSync.WaitAsync().ConfigureAwait(true);
        try
        {
            SavePlaybackPosition();
            DetachStreamProvider();
            ResetStreamingTimeline();
            currentVideo = null;
            RefreshFileActions();
            subtitleFiles = null;
            ErrorMessage = null;
            ScalingQuality = settingsStore.PreferredVideoScaling;
            DownscalingQuality = settingsStore.PreferredVideoDownscaling;
            DebandingQuality = settingsStore.VideoDebanding;
            PreferHardwareProcessing = settingsStore.PreferHardwareVideoProcessing;
            EnableHdrPassthrough = settingsStore.EnableHdrPassthrough;
            DisplayPanelBitDepth = settingsStore.DisplayPanelBitDepth;
            ToneProfiles.Reload(settingsStore.ToneMapping);
            ApplySubtitleBrightness(saved: null);
            RebuildShaderPackageOptions(video: null, selectedOverride: null);
            subtitleSourceKinds.Clear();
            IsSubtitleBrowserOpen = false;
            ResetTranscodeMenu();
            IsTranscodeOffered = false;
            MediaTitle = streamUri.Host;
            Playlist = [];
            NavigationParameter = null;
            OnPropertyChanged(nameof(CanBrowseServerSubtitles));

            bool isDash = streamLink.Sources.Count == 1 &&
                streamUri.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase);
            bool isHls = streamLink.Sources.Count == 1 &&
                (streamUri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                 streamUri.AbsolutePath.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase));
            string streamKind = isDash ? "DASH" : isHls ? "HLS" : "HTTP media";
            IStreamingMediaSourceProvider? streamingProvider = null;
            HttpMediaSourceProvider? httpProvider = null;
            var playerOpened = false;
            Volatile.Write(ref streamOpenInProgress, 1);
            IsStreamLoading = true;
            UpdateStreamingLoadStage(StreamingLoadStage.Connecting);
            try
            {
                await playbackController.MusicOutputReleased.ConfigureAwait(true);
                if (disposed)
                    return;
                if (MediaDecodeSessionFactory.IsRegistered)
                {
                    if (isDash || isHls)
                        throw new NotSupportedException("This browser media session does not play adaptive streams.");
                    currentStreamUri = streamUri;
                    Uri main = streamLink.Sources[0];
                    foreach (Uri source in streamLink.Sources)
                    {
                        await Player.OpenAsync(MediaDecodeSessionFactory.Create(new(source.AbsoluteUri, playerOptions)))
                            .ConfigureAwait(true);
                        playerOpened = true;
                        if (Player.HasVideo)
                        {
                            main = source;
                            break;
                        }
                    }

                    if (!Player.HasVideo && streamLink.Sources.Count > 1)
                        await Player.OpenAsync(MediaDecodeSessionFactory.Create(new(main.AbsoluteUri, playerOptions)))
                            .ConfigureAwait(true);
                    await AttachHttpAudioAsync(streamLink.Sources.Where(source => source != main)).ConfigureAwait(true);
                }
                else
                {
                IMediaSourceProvider provider;
                if (isDash)
                {
                    provider = new DashMediaSourceProvider(streamUri.AbsoluteUri);
                }
                else if (isHls)
                {
                    provider = new HlsMediaSourceProvider(streamUri.AbsoluteUri);
                }
                else
                {
                    httpProvider = new HttpMediaSourceProvider(streamLink.Sources);
                    provider = httpProvider;
                }

                streamingProvider = (IStreamingMediaSourceProvider)provider;
                currentStreamProvider = streamingProvider;
                currentStreamUri = streamUri;
                streamingProvider.TimelineChanged += OnStreamingTimelineChanged;
                streamingProvider.LoadStageChanged += OnStreamingLoadStageChanged;
                UpdateStreamingLoadStage(streamingProvider.LoadStage);
                await Player
                    .OpenOwnedAsync(provider)
                    .ConfigureAwait(true);
                playerOpened = true;
                if (httpProvider is not null)
                    await AttachHttpAudioAsync(httpProvider.AdditionalSources).ConfigureAwait(true);
                }
            }
            catch (OperationCanceledException)
            {
                DetachStreamProvider();
                currentStreamUri = null;
                if (playerOpened)
                    await Player.StopAsync().ConfigureAwait(true);
                return;
            }
            catch (Exception exception)
            {
                DetachStreamProvider();
                currentStreamUri = null;
                if (playerOpened)
                    await Player.StopAsync().ConfigureAwait(true);
                string detail = AppErrorReporter.LogAndGetMessage(
                    "Video.Playback",
                    $"Could not open {streamKind} stream {streamUri.AbsoluteUri}.",
                    exception);
                ErrorMessage = string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.Resources.VideoPlayer_StreamOpenFailedFormat,
                    streamKind,
                    detail);
                return;
            }
            finally
            {
                Volatile.Write(ref streamOpenInProgress, 0);
                if (streamingProvider is not null)
                    streamingProvider.LoadStageChanged -= OnStreamingLoadStageChanged;
                IsStreamLoading = false;
                StreamLoadingText = string.Empty;
            }

            ApplyStreamingTimeline(streamingProvider?.Timeline ?? default);
            DurationSeconds = Player.Duration.TotalSeconds;
            Player.Volume = (float)Volume;
            Player.Speed = Speed;
            RefreshTracks();
            RefreshVideoRenditions();
            UpdatePictureAspectRatio();
            UpdatePreferredOrientation();
            OfferPictureInPicture();
            ApplyDefaultSubtitle(saved: null);

            if (PlatformServices.CanStartPlayback?.Invoke() ?? true)
                Player.Play();
        }
        finally
        {
            openSync.Release();
        }
    }

    private async Task AttachHttpAudioAsync(IEnumerable<Uri> sources)
    {
        AudioTrack? firstTrack = null;
        foreach (Uri source in sources)
        {
            IReadOnlyList<AudioTrack> tracks = await Player
                .AddExternalAudioAsync(source.AbsoluteUri)
                .ConfigureAwait(true);
            firstTrack ??= tracks.FirstOrDefault(track => track.IsDefault) ?? tracks.FirstOrDefault();
        }

        if (Player.ActiveAudioTrack is null && firstTrack is not null)
            Player.SelectAudioTrack(firstTrack);
    }

    private async Task OpenCoreAsync(
        SourcedVideo sourcedVideo,
        IReadOnlyList<SourcedVideo> siblings,
        bool resume,
        bool persistPlaybackState)
    {
        // The page is reused when another entry of its list is picked, so the video being replaced
        // gets its resume point written before currentVideo moves on.
        SavePlaybackPosition();
        this.persistPlaybackState = persistPlaybackState;
        DetachStreamProvider();
        currentStreamUri = null;
        ResetStreamingTimeline();

        ErrorMessage = null;
        ScalingQuality = settingsStore.PreferredVideoScaling;
        DownscalingQuality = settingsStore.PreferredVideoDownscaling;
        DebandingQuality = settingsStore.VideoDebanding;
        PreferHardwareProcessing = settingsStore.PreferHardwareVideoProcessing;
        EnableHdrPassthrough = settingsStore.EnableHdrPassthrough;
        DisplayPanelBitDepth = settingsStore.DisplayPanelBitDepth;
        ToneProfiles.Reload(settingsStore.ToneMapping);
        var video = sourcedVideo.Value;
        currentVideo = video;
        currentSourceId = sourcedVideo.SourceId;
        RefreshFileActions();
        var media = new MediaReference(currentSourceId, video.Path);
        var saved = persistPlaybackState ? playbackStates.Get(media) : null;
        ApplySubtitleBrightness(saved);
        ApplyShaderPackageOverride(video, saved?.ShaderPackageOverride);
        subtitleSourceKinds.Clear();
        IsSubtitleBrowserOpen = false;
        ResetTranscodeMenu(saved);
        MediaTitle = video.DisplayName ?? string.Empty;
        Playlist = siblings
            .Select(item => new VideoListItemViewModel(
                item.Value,
                PlayVideoCommand,
                string.Equals(item.Value.Path, video.Path, StringComparison.Ordinal)))
            .ToList();
        NavigationParameter = new VideoPlaybackRequest(sourcedVideo, siblings);

        try
        {
            await playbackController.MusicOutputReleased.ConfigureAwait(true);
            if (disposed)
                return;
            var platform = await mediaResolver.ResolvePlatformAsync(media).ConfigureAwait(true);
            subtitleFiles = mediaResolver.ResolveSubtitleBrowser(currentSourceId);
            IsTranscodeOffered = platform is IMediaSourceProviderFactory { SupportsTranscoding: true };
            OnPropertyChanged(nameof(CanBrowseServerSubtitles));
            await Player
                .OpenAsync(MediaSourceProviders.CreateDecodeSession(playerOptions, media, mediaResolver, video.MediaKind, video.DiscTitle,
                    new RemoteTranscodeIntent(settingsStore.RemoteTranscodeSettings, transcodeOverride)))
                .ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ErrorMessage = AppErrorReporter.LogAndGetMessage(
                "Video.Playback",
                $"Could not open video {video.Path}.",
                exception);
            return;
        }

        await AttachSidecarsAsync(video).ConfigureAwait(true);

        DurationSeconds = Player.Duration.TotalSeconds;
        Player.Volume = (float)Volume;
        Player.Speed = Speed;
        RefreshTracks();
        RefreshVideoRenditions();
        UpdatePictureAspectRatio();
        UpdatePreferredOrientation();

        // The picture this page offers has the shape of whichever video is open.
        OfferPictureInPicture();

        await RestoreSubtitlesAsync(video, saved).ConfigureAwait(true);
        if (disposed || !ReferenceEquals(currentVideo, video))
        {
            return;
        }

        ApplySavedTracks(saved);
        ApplyDefaultSubtitle(saved);

        var resumeTo = resume && VideoPlaybackProgressPolicy.ShouldOfferResume(saved, DurationSeconds)
            ? saved!.PositionSeconds
            : 0;

        // Play first, then seek: a seek that overlaps the move to Playing captures the state it
        // finds, so the other order can leave the engine parked at the resume point.
        if (PlatformServices.CanStartPlayback?.Invoke() ?? true)
        {
            Player.Play();
        }

        if (resumeTo > 0)
        {
            Seek(resumeTo);
        }
    }

    /// <summary>
    /// Picks a subtitle for a video that has no choice of its own on record: the track labeled the
    /// same as the one last used in this session, or failing that the first the video offers.
    /// </summary>
    private void ApplyDefaultSubtitle(VideoPlaybackState? saved)
    {
        if (SubtitleTracks.Count == 0 || SelectedSubtitleTrack is not null || saved is { SubtitlesOff: true })
        {
            return;
        }

        // Not the user's choice, so it neither becomes the session preference nor is written back
        // as this video's own.
        restoringTracks = true;
        try
        {
            SelectedSubtitleTrack = SubtitleTracks.FirstOrDefault(track =>
                string.Equals(track.DisplayName, preferredSubtitleName, StringComparison.Ordinal))
                ?? SubtitleTracks[0];
        }
        finally
        {
            restoringTracks = false;
        }
    }

    /// <summary>Re-applies the tracks the user last chose for this video.</summary>
    private void ApplySavedTracks(VideoPlaybackState? saved)
    {
        if (saved is null)
        {
            return;
        }

        restoringTracks = true;
        try
        {
            if (saved.AudioStreamIndex is { } audioIndex &&
                AudioTracks.FirstOrDefault(track => !track.IsExternal && track.StreamIndex == audioIndex) is { } audio)
            {
                SelectedAudioTrack = audio;
            }

            if (saved.SubtitlesOff)
            {
                SelectedSubtitleTrack = null;
            }
            else if (FindSavedSubtitle(saved) is { } subtitle)
            {
                SelectedSubtitleTrack = subtitle;
            }
        }
        finally
        {
            restoringTracks = false;
        }
    }

    /// <summary>
    /// The track a saved choice names. A choice made before external subtitles were remembered
    /// carries no source, and so can only mean one of the video's own streams.
    /// </summary>
    private SubtitleTrack? FindSavedSubtitle(VideoPlaybackState saved)
    {
        if (saved.SubtitleStreamIndex is not { } index)
        {
            return null;
        }

        return saved.SubtitleSource is { Length: > 0 } source
            ? SubtitleTracks.FirstOrDefault(track =>
                string.Equals(track.Source, source, StringComparison.Ordinal) && track.StreamIndex == index)
            : SubtitleTracks.FirstOrDefault(track => !track.IsExternal && track.StreamIndex == index);
    }

    /// <summary>
    /// Writes the display progress of the video on screen. Completing a video stores its full
    /// duration so the library can show it as watched; resume prompting filters that state out.
    /// </summary>
    private void SavePlaybackPosition(bool reachedEnd = false)
    {
        if (!persistPlaybackState || currentVideo is null)
        {
            return;
        }

        var position = Player.Position.TotalSeconds;
        var duration = Player.Duration.TotalSeconds;
        if (!double.IsFinite(duration) || duration <= 0)
        {
            duration = currentVideo.Duration.TotalSeconds;
        }

        var media = new MediaReference(currentSourceId, currentVideo.Path);
        var state = playbackStates.Get(media) ?? new VideoPlaybackState();
        VideoPlaybackProgressPolicy.Record(
            state,
            position,
            duration,
            reachedEnd || Player.State == PlaybackState.EndOfStream);
        WriteSubtitleBrightness(state);
        playbackStates.Save(media, state);
    }

    private void SaveShaderPackagePreference()
    {
        if (!persistPlaybackState || !isShaderPackageLoaded || currentVideo is null)
            return;

        var media = new MediaReference(currentSourceId, currentVideo.Path);
        VideoPlaybackState state = playbackStates.Get(media) ?? new VideoPlaybackState();
        state.ShaderPackageOverride = ShaderPackageOverride;
        WriteSubtitleBrightness(state);
        playbackStates.Save(media, state);
    }

    private void ApplySubtitleBrightness(VideoPlaybackState? saved)
    {
        restoringSubtitleBrightness = true;
        try
        {
            SubtitleBrightness mode = saved?.SubtitleBrightnessMode ?? DefaultSubtitleBrightnessMode;
            SelectedSubtitleBrightnessOption = SubtitleBrightnessOptions.FirstOrDefault(option => option.Mode == mode)
                ?? SubtitleBrightnessOptions.First(option => option.Mode == DefaultSubtitleBrightnessMode);
        }
        finally
        {
            restoringSubtitleBrightness = false;
        }
    }

    private void SaveSubtitleBrightnessPreference()
    {
        if (!persistPlaybackState || restoringSubtitleBrightness || currentVideo is null)
            return;

        var media = new MediaReference(currentSourceId, currentVideo.Path);
        VideoPlaybackState state = playbackStates.Get(media) ?? new VideoPlaybackState();
        WriteSubtitleBrightness(state);
        playbackStates.Save(media, state);
    }

    private void WriteSubtitleBrightness(VideoPlaybackState state)
    {
        state.SubtitleBrightnessMode = SubtitleBrightnessMode;
    }

    private void ApplyShaderPackageOverride(DbVideoFile video, string? savedOverride)
    {
        bool installed = string.IsNullOrEmpty(savedOverride) ||
            string.Equals(savedOverride, VideoShaderPackageCatalog.DisabledOverride, StringComparison.Ordinal) ||
            shaderPackages?.Packages.Any(package => package.Id == savedOverride) == true;
        RebuildShaderPackageOptions(video, installed ? savedOverride : null);
    }

    private void RebuildShaderPackageOptions(DbVideoFile? video, string? selectedOverride)
    {
        isShaderPackageLoaded = false;
        int sourceResolution = video is null ? 0 : Math.Min(video.Width, video.Height);
        var options = new List<VideoShaderPackageOption>
        {
            new(null, Localized("VideoPlayer_ScalingFilterAutomatic", "Automatic")),
            new(VideoShaderPackageCatalog.DisabledOverride, Localized("VideoPlayer_ScalingFilterOff", "Off")),
        };
        foreach (VideoShaderPackage package in shaderPackages?.Packages ?? [])
        {
            string title = package.Manifest.Name;
            if (!package.IsEnabled)
            {
                title += " " + Localized("VideoPlayer_ScalingFilterDisabledSuffix", "(disabled automatically)");
            }
            else if (sourceResolution > 0 &&
                package.MaximumSourceResolution > 0 &&
                sourceResolution > package.MaximumSourceResolution)
            {
                title += " " + Localized("VideoPlayer_ScalingFilterResolutionSuffix", "(above automatic limit)");
            }
            options.Add(new VideoShaderPackageOption(package.Id, title));
        }

        bool requestedPackageMissing = !string.IsNullOrEmpty(selectedOverride) &&
            !string.Equals(
                selectedOverride,
                VideoShaderPackageCatalog.DisabledOverride,
                StringComparison.Ordinal) &&
            options.All(option => !string.Equals(option.Value, selectedOverride, StringComparison.Ordinal));
        ShaderPackageOptions = options;
        OnPropertyChanged(nameof(ShaderPackageOptions));
        OnPropertyChanged(nameof(HasShaderPackageChoices));
        SelectedShaderPackage = options.FirstOrDefault(option =>
            string.Equals(option.Value, selectedOverride, StringComparison.Ordinal)) ?? options[0];
        ShaderPackageOverride = SelectedShaderPackage.Value;
        isShaderPackageLoaded = true;
        OnPropertyChanged(nameof(ShaderPackageOverride));
        if (requestedPackageMissing)
            SaveShaderPackagePreference();
    }

    private void OnShaderPackagesChanged(object? sender, EventArgs eventArgs)
    {
        void Rebuild() => RebuildShaderPackageOptions(currentVideo, ShaderPackageOverride);
        if (Dispatcher.UIThread.CheckAccess())
            Rebuild();
        else
            Dispatcher.UIThread.Post(Rebuild);
    }

    public override void OnLocalizedTextChanged()
    {
        base.OnLocalizedTextChanged();
        RebuildShaderPackageOptions(currentVideo, ShaderPackageOverride);
        if (IsStreamLoading && currentStreamProvider is { } provider)
            UpdateStreamingLoadStage(provider.LoadStage);
        OnPropertyChanged(nameof(TimeText));
    }

    private void SaveAudioTrackPreference()
    {
        if (!persistPlaybackState || restoringTracks || currentVideo is null)
        {
            return;
        }

        var media = new MediaReference(currentSourceId, currentVideo.Path);
        var state = playbackStates.Get(media) ?? new VideoPlaybackState();
        state.AudioStreamIndex = SelectedAudioTrack is { IsExternal: false } audio ? audio.StreamIndex : null;
        playbackStates.Save(media, state);
    }

    /// <summary>
    /// Remembers the subtitle picked for this video, including which file an external subtitle came
    /// out of, so the same script is back on screen the next time the video is opened.
    /// </summary>
    private void SaveSubtitleTrackPreference()
    {
        if (!persistPlaybackState || restoringTracks || currentVideo is null)
        {
            return;
        }

        var media = new MediaReference(currentSourceId, currentVideo.Path);
        var state = playbackStates.Get(media) ?? new VideoPlaybackState();

        // An empty list means the saved script could not be re-attached this time (the file moved,
        // the server was out of reach). Leaving the record alone is what lets it come back later.
        if (SubtitleTracks.Count > 0)
        {
            state.SubtitleStreamIndex = SelectedSubtitleTrack?.StreamIndex;
            state.SubtitleSource = SelectedSubtitleTrack?.Source;
            state.SubtitleSourceKind = SelectedSubtitleTrack?.Source is { } source
                ? subtitleSourceKinds.GetValueOrDefault(source, SubtitleSourceKind.Indexed)
                : SubtitleSourceKind.Indexed;
            state.SubtitlesOff = SelectedSubtitleTrack is null;
        }

        playbackStates.Save(media, state);
    }

    /// <summary>
    /// Puts back the subtitles that are not in the video's own file: every file imported for it,
    /// and the server file it was last watched with. Each is best-effort, because one that can no
    /// longer be read should cost the choice and nothing more.
    /// </summary>
    private async Task RestoreSubtitlesAsync(DbVideoFile video, VideoPlaybackState? saved)
    {
        foreach (var path in persistSubtitleImports
            ? SubtitleCache.Imports(new MediaReference(currentSourceId, video.Path))
            : Array.Empty<string>())
        {
            try
            {
                await AttachCachedSubtitleAsync(path).ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                AppLog.Warning("Video.Subtitles", $"Could not restore cached subtitles {path}.", exception);
            }
        }

        if (subtitleFiles is null ||
            saved is not { SubtitleSourceKind: SubtitleSourceKind.Remote, SubtitleSource: { Length: > 0 } source })
        {
            return;
        }

        try
        {
            await AttachSubtitleFileAsync(
                source,
                await subtitleFiles.ReadAsync(source).ConfigureAwait(true),
                SubtitleSourceKind.Remote).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            AppLog.Warning("Video.Subtitles", $"Could not restore remote subtitles {source}.", exception);
        }
    }

    /// <summary>
    /// Hands the indexed sidecar files to the engine. Each is best-effort: one that cannot be
    /// opened at play time (moved or unreadable) is skipped rather than failing the whole video.
    /// </summary>
    private async Task AttachSidecarsAsync(DbVideoFile video)
    {
        foreach (var path in video.ExternalAudioList)
        {
            try
            {
                await Player.AddExternalAudioAsync(path).ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                AppLog.Warning("Video.Audio", $"Could not attach sidecar audio {path}.", exception);
            }
        }

        foreach (var path in video.ExternalSubtitleList)
        {
            try
            {
                await Player.AddExternalSubtitleAsync(path).ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                AppLog.Warning("Video.Subtitles", $"Could not attach sidecar subtitles {path}.", exception);
            }
        }
    }

    /// <summary>
    /// Picks a subtitle file off this device and attaches it to the current session.
    /// </summary>
    private async Task AddLocalSubtitleAsync()
    {
        if (dialogService is null || currentVideo is not { } video)
        {
            return;
        }

        try
        {
            if (await dialogService.PickSubtitleFileAsync().ConfigureAwait(true) is not { } picked)
            {
                return;
            }

            if (!ReferenceEquals(currentVideo, video))
            {
                return;
            }

            if (!persistSubtitleImports)
            {
                SelectedSubtitleTrack = await AttachSubtitleFileAsync(
                    picked.Name, picked.Content, SubtitleSourceKind.Indexed).ConfigureAwait(true);
                return;
            }

            var cached = SubtitleCache.Store(
                new MediaReference(currentSourceId, video.Path),
                picked.Name,
                picked.Content);
            SelectedSubtitleTrack = await AttachCachedSubtitleAsync(cached, picked.Content).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ErrorMessage = AppErrorReporter.LogAndGetMessage(
                "Video.Subtitles",
                "Could not add subtitles from this device.",
                exception);
        }
    }

    /// <summary>Opens the server's subtitle browser on the folder the video is playing out of.</summary>
    private async Task OpenSubtitleBrowserAsync()
    {
        IsPlaylistOpen = false;
        IsSubtitleBrowserOpen = true;
        await LoadSubtitleFolderAsync(currentVideo?.FolderPath).ConfigureAwait(true);
    }

    /// <summary>
    /// Lists one folder of the server's video library, or its roots when no folder is named. A
    /// folder the server will not open (one the video was indexed from but is no longer served)
    /// falls back to the roots rather than leaving the browser empty.
    /// </summary>
    private async Task LoadSubtitleFolderAsync(string? folderPath)
    {
        if (subtitleFiles is null)
        {
            return;
        }

        IsSubtitleBrowserLoading = true;
        try
        {
            SubtitleFileListing listing;
            try
            {
                listing = await subtitleFiles.ListAsync(folderPath).ConfigureAwait(true);
            }
            catch (Exception exception) when (folderPath is not null)
            {
                AppLog.Warning(
                    "Video.Subtitles",
                    $"Could not list subtitle folder {folderPath}; falling back to the library root.",
                    exception);
                listing = await subtitleFiles.ListAsync(null).ConfigureAwait(true);
            }

            SubtitleBrowserItems = listing.Entries
                .Select(entry => new SubtitleBrowserItemViewModel(entry, ChooseSubtitleEntryCommand))
                .ToList();
            SubtitleBrowserPath =
            [
                new SubtitleBrowserCrumbViewModel(
                    Strings.Resources.Video_LibraryRoot,
                    null,
                    BrowseSubtitleFolderCommand),
                .. listing.Ancestors.Select(step =>
                    new SubtitleBrowserCrumbViewModel(step.Name, step.Path, BrowseSubtitleFolderCommand)),
            ];
        }
        catch (Exception exception)
        {
            ErrorMessage = AppErrorReporter.LogAndGetMessage(
                "Video.Subtitles",
                $"Could not list subtitle folder {folderPath ?? "<root>"}.",
                exception);
            IsSubtitleBrowserOpen = false;
        }
        finally
        {
            IsSubtitleBrowserLoading = false;
        }
    }

    /// <summary>Opens a browsed folder, or fetches a browsed subtitle file and shows it.</summary>
    private async Task ChooseSubtitleEntryAsync(SubtitleFileEntry? entry)
    {
        if (subtitleFiles is null || entry is null)
        {
            return;
        }

        if (entry.IsFolder)
        {
            await LoadSubtitleFolderAsync(entry.Path).ConfigureAwait(true);
            return;
        }

        var video = currentVideo;
        try
        {
            var content = await subtitleFiles.ReadAsync(entry.Path).ConfigureAwait(true);
            if (!ReferenceEquals(currentVideo, video))
            {
                return;
            }

            SelectedSubtitleTrack = await AttachSubtitleFileAsync(
                entry.Path,
                content,
                SubtitleSourceKind.Remote).ConfigureAwait(true);
            IsSubtitleBrowserOpen = false;
        }
        catch (Exception exception)
        {
            ErrorMessage = AppErrorReporter.LogAndGetMessage(
                "Video.Subtitles",
                $"Could not load subtitles {entry.Path}.",
                exception);
        }
    }

    /// <summary>
    /// Hands a script the page already holds to the engine and makes it selectable. The bytes go in
    /// rather than the path because the session's own source cannot reach either kind of file: one
    /// lives on this device while the video is on a server, the other is only served over the API.
    /// </summary>
    private async Task<SubtitleTrack> AttachSubtitleScriptAsync(string source, byte[] content, SubtitleSourceKind kind)
    {
        using var stream = new MemoryStream(content, writable: false);
        var track = await Player.AddSubtitleScriptAsync(source, stream).ConfigureAwait(true);
        subtitleSourceKinds[source] = kind;

        // The engine announces the new track on a posted callback, and the picker has to hold it
        // before the selection below can land on it.
        RefreshTracks();
        return track;
    }

    private async Task<SubtitleTrack> AttachCachedSubtitleAsync(string source, byte[]? content = null)
    {
        content ??= await File.ReadAllBytesAsync(source).ConfigureAwait(true);
        return await AttachSubtitleFileAsync(source, content, SubtitleSourceKind.Cached).ConfigureAwait(true);
    }

    private async Task<SubtitleTrack> AttachSubtitleFileAsync(
        string source,
        byte[] content,
        SubtitleSourceKind kind)
    {
        if (SubtitleLoader.IsScriptFile(source))
            return await AttachSubtitleScriptAsync(source, content, kind).ConfigureAwait(true);

        IReadOnlyList<SubtitleTrack> tracks = await Player.AddSubtitleFileAsync(source, content).ConfigureAwait(true);
        subtitleSourceKinds[source] = kind;
        RefreshTracks();
        return tracks[0];
    }

    /// <summary>Polled by the view's UI timer; keeps the transport in step with the engine.</summary>
    public void Tick(bool isScrubbing)
    {
        if (disposed)
        {
            return;
        }

        if (!isScrubbing)
        {
            PositionSeconds = Player.Position.TotalSeconds;
        }

        // The engine is the authority on whether it is running: a state change that raced the open
        // (the resume seek against the first Play) would otherwise leave the button showing the
        // wrong glyph until it was pressed twice.
        UpdateIsPlaying(Player.State);

        if (Math.Abs(DurationSeconds - Player.Duration.TotalSeconds) > 0.01)
        {
            DurationSeconds = Player.Duration.TotalSeconds;
        }

        BufferedSeconds = Player.BufferedSeconds;

        var chapter = Player.CurrentChapter;
        if (!ReferenceEquals(chapter, SelectedChapter))
        {
            suppressTrackSync = true;
            try
            {
                SelectedChapter = chapter;
            }
            finally
            {
                suppressTrackSync = false;
            }
        }

        if (AreStatisticsVisible)
        {
            StatisticsText = Player.GetStatistics().ToDisplayString();
        }
    }

    public void Seek(double seconds) =>
        Player.Seek(TimeSpan.FromSeconds(Math.Clamp(seconds, SeekMinimumSeconds, SeekMaximumSeconds)));

    /// <summary>Moves the given number of seconds along the video, without running off either end.</summary>
    public void SeekRelative(double seconds) =>
        Seek(Math.Clamp(Player.Position.TotalSeconds + seconds, SeekMinimumSeconds, SeekMaximumSeconds));

    private async Task GoLiveAsync()
    {
        if (!IsLiveStream || currentStreamUri is not { } streamUri)
            return;

        if (CanSeek)
        {
            Seek(Math.Max(LiveWindowStartSeconds, LiveWindowEndSeconds - 0.1));
            Player.Play();
            return;
        }

        await OpenStreamAsync(streamUri).ConfigureAwait(true);
    }

    /// <summary>Nudges the volume, and remembers where it was so the mute has somewhere to return to.</summary>
    public void AdjustVolume(double delta)
    {
        Volume = Math.Clamp(Volume + delta, 0d, 1d);
        if (!IsMuted)
        {
            volumeBeforeMute = Volume;
        }
    }

    /// <summary>Steps the given number of places along the speed menu, stopping at either end.</summary>
    public void StepSpeed(int delta)
    {
        var index = SelectedSpeedOption is { } current ? IndexOf(SpeedOptions, current) : -1;
        if (index < 0)
        {
            index = SpeedOptions.Count / 2;
        }

        SelectedSpeedOption = SpeedOptions[Math.Clamp(index + delta, 0, SpeedOptions.Count - 1)];
    }

    /// <summary>
    /// Runs the picture fast while a finger rests on it, as a phone player does, and puts it back on
    /// the chosen rate when the finger lifts. The speed menu is left alone: this is not a choice.
    /// </summary>
    public void SetSpeedBoost(bool boosted)
    {
        if (IsSpeedBoosted == boosted)
        {
            return;
        }

        IsSpeedBoosted = boosted;
        Player.Speed = boosted ? Speed * HoldSpeedMultiplier : Speed;
    }

    /// <summary>
    /// Takes over the OS transport controls for as long as the page is on screen, so the lock
    /// screen, the notification and the media keys drive the video instead of the music queue that
    /// was stood down to make room for it.
    /// </summary>
    public void TakeSystemMediaControls(SystemMediaSession session)
    {
        ReleaseSystemMediaControls();

        mediaSession = session;
        session.PlayPauseRequested += OnSystemPlayPauseRequested;
        session.NextRequested += OnSystemNextRequested;
        session.PreviousRequested += OnSystemPreviousRequested;
        session.SeekRequested += OnSystemSeekRequested;

        // Videos carry no cover, so the album art of the suspended track has to be cleared as well.
        session.SetArtwork(null);
        PushSystemMediaMetadata();
        PushSystemMediaState();
        PushSystemMediaProgress();
        OfferPictureInPicture();
    }

    /// <summary>Gives the OS transport controls back to the music transport.</summary>
    public void ReleaseSystemMediaControls()
    {
        if (mediaSession is not { } session)
        {
            return;
        }

        mediaSession = null;
        session.PlayPauseRequested -= OnSystemPlayPauseRequested;
        session.NextRequested -= OnSystemNextRequested;
        session.PreviousRequested -= OnSystemPreviousRequested;
        session.SeekRequested -= OnSystemSeekRequested;
        session.Dispose();
        OfferPictureInPicture();
    }

    private void OnSystemPlayPauseRequested() => Player.TogglePause();

    private void OnSystemNextRequested() => StepPlaylist(1);

    private void OnSystemPreviousRequested() => StepPlaylist(-1);

    private void OnSystemSeekRequested(TimeSpan position) =>
        Seek(SeekMinimumSeconds + position.TotalSeconds);

    private void PushSystemMediaMetadata() =>
        mediaSession?.SetNowPlaying(MediaTitle, string.Empty, string.Empty);

    private void PushSystemMediaState() => mediaSession?.SetPlaybackState(IsPlaying);

    private void PushSystemMediaProgress() => mediaSession?.SetPlaybackProgress(
        TimeSpan.FromSeconds(Math.Max(0, PositionSeconds - SeekMinimumSeconds)),
        TimeSpan.FromSeconds(Math.Max(0, SeekMaximumSeconds - SeekMinimumSeconds)));

    partial void OnMediaTitleChanged(string value) => PushSystemMediaMetadata();

    partial void OnIsPlayingChanged(bool value)
    {
        PushSystemMediaState();
        setKeepScreenOn(value);
        OfferPictureInPicture();
    }

    /// <summary>
    /// Lets a host that shrinks the app follow the user out of it while the video runs, so leaving
    /// keeps the picture instead of putting it away. Only while this page is the one on screen,
    /// which is what holding the OS transport controls amounts to: a page left on the back stack
    /// would otherwise offer a picture the app is no longer showing.
    /// </summary>
    private void OfferPictureInPicture() =>
        PlatformServices.PictureInPicture?.Offer(
            IsPlaying && !disposed && mediaSession is not null
                ? new PictureInPictureRequest(this, PictureAspectRatio)
                : null);

    partial void OnPositionSecondsChanged(double value) => PushSystemMediaProgress();

    partial void OnDurationSecondsChanged(double value) => PushSystemMediaProgress();

    partial void OnSelectedVideoTranscodeChanged(VideoSettingsOption value) => MarkTranscodePending();

    partial void OnSelectedAudioTranscodeChanged(VideoSettingsOption value) => MarkTranscodePending();

    partial void OnTranscodeVideoBitrateChanged(decimal value) => MarkTranscodePending();

    partial void OnTranscodeVideoHeightChanged(decimal? value) => MarkTranscodePending();

    partial void OnTranscodeAudioBitrateChanged(decimal value) => MarkTranscodePending();

    partial void OnHasPendingTranscodeChangesChanged(bool value) => ApplyTranscodeCommand.NotifyCanExecuteChanged();

    /// <remarks>
    /// Each keystroke in a bitrate box raises a change, so the terms are held until asked for rather
    /// than reopening the stream on every digit.
    /// </remarks>
    private void MarkTranscodePending()
    {
        if (isTranscodeLoaded)
        {
            HasPendingTranscodeChanges = true;
        }
    }

    private void ApplyTranscodeOverride()
    {
        if (!isTranscodeLoaded)
        {
            return;
        }

        HasPendingTranscodeChanges = false;
        var next = new RemoteTranscodeOverride(
            TranscodeChoice(SelectedVideoTranscode),
            TranscodeChoice(SelectedAudioTranscode),
            (long)Math.Max(0, TranscodeVideoBitrate * 1_000_000),
            (long)Math.Max(0, TranscodeAudioBitrate * 1_000),
            (int)Math.Clamp(TranscodeVideoHeight ?? 0, 0, 8192));
        if (next != transcodeOverride)
        {
            transcodeOverride = next;
            if (persistPlaybackState && currentVideo is { } video)
            {
                var media = new MediaReference(currentSourceId, video.Path);
                var state = playbackStates.Get(media) ?? new VideoPlaybackState();
                state.TranscodeOverride = next.IsEmpty ? null : next;
                playbackStates.Save(media, state);
            }

            OnTranscodeChanged(this, EventArgs.Empty);
        }
    }

    private void ResetTranscodeMenu(VideoPlaybackState? saved = null)
    {
        transcodeOverride = saved?.TranscodeOverride ?? RemoteTranscodeOverride.None;
        isTranscodeLoaded = false;
        SelectedVideoTranscode = FindTranscodeOption(VideoTranscodeOptions, transcodeOverride.Video);
        SelectedAudioTranscode = FindTranscodeOption(AudioTranscodeOptions, transcodeOverride.Audio);
        TranscodeVideoBitrate = transcodeOverride.VideoBitrate / 1_000_000m;
        TranscodeVideoHeight = transcodeOverride.VideoHeight > 0 ? transcodeOverride.VideoHeight : null;
        TranscodeAudioBitrate = transcodeOverride.AudioBitrate / 1_000m;
        HasPendingTranscodeChanges = false;
        isTranscodeLoaded = true;
    }

    /// <remarks>
    /// A source that will not re-encode would be reopened on the terms it already has, so a change
    /// meant for another library leaves the video on screen alone.
    /// </remarks>
    private void OnTranscodeChanged(object? sender, EventArgs e)
    {
        if (IsTranscodeOffered)
        {
            _ = RestartStreamAsync();
        }
    }

    /// <summary>
    /// Opens the video again so the server is asked on the new terms.
    /// </summary>
    /// <remarks>
    /// Nothing about a stream already in flight can be changed: what a track is was settled when the
    /// socket named it. Reopening costs the buffered picture and nothing else, because the position
    /// and the chosen tracks are restored behind it.
    /// </remarks>
    private async Task RestartStreamAsync()
    {
        await openSync.WaitAsync().ConfigureAwait(true);
        try
        {
            await RestartStreamCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            openSync.Release();
        }
    }

    private async Task RestartStreamCoreAsync()
    {
        if (disposed || currentVideo is not { } video)
        {
            return;
        }

        double position = Player.Position.TotalSeconds;
        bool wasPlaying = IsPlaying;

        try
        {
            var media = new MediaReference(currentSourceId, video.Path);
            await mediaResolver.ResolvePlatformAsync(media).ConfigureAwait(true);
            await Player
                .OpenAsync(MediaSourceProviders.CreateDecodeSession(playerOptions, media, mediaResolver, video.MediaKind, video.DiscTitle,
                    new RemoteTranscodeIntent(settingsStore.RemoteTranscodeSettings, transcodeOverride)))
                .ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ErrorMessage = AppErrorReporter.LogAndGetMessage(
                "Video.Playback",
                $"Could not restart video {video.Path}.",
                exception);
            return;
        }

        if (disposed || !ReferenceEquals(currentVideo, video))
        {
            return;
        }

        await AttachSidecarsAsync(video).ConfigureAwait(true);
        DurationSeconds = Player.Duration.TotalSeconds;
        Player.Volume = (float)Volume;
        Player.Speed = Speed;
        RefreshTracks();

        var saved = persistPlaybackState
            ? playbackStates.Get(new MediaReference(currentSourceId, video.Path))
            : null;
        subtitleSourceKinds.Clear();
        await RestoreSubtitlesAsync(video, saved).ConfigureAwait(true);
        if (disposed || !ReferenceEquals(currentVideo, video))
        {
            return;
        }

        ApplySavedTracks(saved);
        ApplyDefaultSubtitle(saved);

        if (position > 0)
        {
            Seek(position);
        }

        if (wasPlaying)
        {
            Player.Play();
        }
    }

    private static bool? TranscodeChoice(VideoSettingsOption? option) => option?.Value switch
    {
        TranscodeOn => true,
        TranscodeOff => false,
        _ => null,
    };

    private static VideoSettingsOption FindTranscodeOption(IReadOnlyList<VideoSettingsOption> options, bool? choice)
    {
        string value = choice switch
        {
            true => TranscodeOn,
            false => TranscodeOff,
            _ => string.Empty,
        };

        return options.First(option => string.Equals(option.Value, value, StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        SavePlaybackPosition();
        DetachStreamProvider();
        disposed = true;
        ReleaseSystemMediaControls();
        setFullScreen(false);
        if (PlatformServices.PictureInPicture is { } pictureInPicture)
        {
            pictureInPicture.ActiveChanged -= OnPictureInPictureHostChanged;
            pictureInPicture.Offer(null);
            if (IsPictureInPicture)
            {
                pictureInPicture.Exit();
            }
        }

        setPreferredOrientation(0);
        setKeepScreenOn(false);
        playbackController.PropertyChanged -= OnPlaybackControllerPropertyChanged;
        settingsStore.RemoteTranscodeSettingsChanged -= OnTranscodeChanged;
        if (shaderPackages is not null)
            shaderPackages.Changed -= OnShaderPackagesChanged;
        Player.StateChanged -= OnStateChanged;
        Player.StallChanged -= OnStallChanged;
        Player.ErrorOccurred -= OnErrorOccurred;
        Player.EndReached -= OnEndReached;
        Player.AudioTracksChanged -= OnAudioTracksChanged;
        Player.AudioTrackChanged -= OnAudioTrackChanged;
        Player.VideoRenditionsChanged -= OnVideoRenditionsChanged;
        Player.VideoRenditionChanged -= OnVideoRenditionChanged;
        Player.SubtitleTracksChanged -= OnSubtitleTracksChanged;
        Player.SubtitleTrackChanged -= OnSubtitleTrackChanged;
        Player.ChaptersChanged -= OnChaptersChanged;
        Player.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await Player.DisposeAsync().ConfigureAwait(true);
    }

    private void OnPlaybackControllerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaybackController.Volume))
        {
            Player.Volume = (float)Volume;
            OnPropertyChanged(nameof(Volume));
            OnPropertyChanged(nameof(IsMuted));
            OnPropertyChanged(nameof(VolumeIconKey));
        }
    }

    partial void OnSpeedChanged(double value) =>
        Player.Speed = IsSpeedBoosted ? value * HoldSpeedMultiplier : value;

    partial void OnSelectedSpeedOptionChanged(SpeedOption? value) => Speed = value?.Value ?? 1.0;

    partial void OnSelectedVideoQualityOptionChanged(VideoQualityOption? value)
    {
        if (isVideoRenditionLoaded && value is not null)
            SelectVideoRendition(value.Renditions, SelectedCameraAngleOption?.Name);
    }

    partial void OnSelectedCameraAngleOptionChanged(VideoAngleOption? value)
    {
        if (isVideoRenditionLoaded && value is not null)
        {
            VideoRendition? target = SelectedVideoQualityOption?.Renditions.FirstOrDefault(
                rendition => string.Equals(rendition.Name, value.Name, StringComparison.Ordinal));
            target ??= Player.VideoRenditions.FirstOrDefault(
                rendition => string.Equals(rendition.Name, value.Name, StringComparison.Ordinal));
            if (target is not null)
                Player.SelectVideoRendition(target);
        }
    }

    partial void OnIsFullScreenChanged(bool value)
    {
        setFullScreen(value);
        UpdatePreferredOrientation();
    }

    partial void OnIsPictureInPictureChanged(bool value)
    {
        if (PlatformServices.PictureInPicture is not { IsAvailable: true } host)
        {
            return;
        }

        // A host that shrinks the app shows this very page as the picture, so everything drawn over
        // it has to go; the picture comes back to a page with its controls where it left them.
        if (host.Kind == PictureInPictureKind.HostManaged)
        {
            AreControlsVisible = !value;
            if (value)
            {
                IsPlaylistOpen = false;
                IsSubtitleBrowserOpen = false;
                AreShortcutsVisible = false;
            }
        }

        if (!value)
        {
            host.Exit();
            return;
        }

        // Full screen and a detached picture are the same picture in two places; the page gives way.
        if (host.Kind == PictureInPictureKind.DetachedView)
        {
            IsFullScreen = false;
        }

        host.Enter(new PictureInPictureRequest(this, PictureAspectRatio));
    }

    /// <summary>Reports the mode being left by the window itself rather than by the page.</summary>
    private void OnPictureInPictureHostChanged(bool active) => IsPictureInPicture = active;

    private void UpdatePictureAspectRatio() =>
        PictureAspectRatio = Player.Info?.DefaultVideoStream is { Height: > 0 } video
            ? video.DisplaySize.Width / video.DisplaySize.Height
            : 0;

    // The corner card has no room for anything but the picture, and the panels would come back with
    // the full view still covering it.
    partial void OnIsMinimizedChanged(bool value)
    {
        if (value)
        {
            IsPlaylistOpen = false;
            IsSubtitleBrowserOpen = false;
            AreShortcutsVisible = false;
            IsFullScreen = false;
        }
        else
        {
            // The deck was last left hidden by the idle timer; a card just maximized has to come
            // back with its controls up.
            AreControlsVisible = true;
        }
    }

    partial void OnIsAutoPlayNextEnabledChanged(bool value) => settingsStore.AutoPlayNextVideo = value;

    partial void OnSubtitleFontSizeChanged(decimal value)
    {
        int size = SubtitleFontSizeSettings.Normalize((int)Math.Round(value));
        if (size != value)
        {
            SubtitleFontSize = size;
            return;
        }

        settingsStore.SubtitleFontSize = size;
    }

    partial void OnSelectedSubtitleFontOptionChanged(SubtitleFontOption? value)
    {
        // The picker clears its selection while its list is replaced.
        if (value is null)
            return;

        PlainTextSubtitleFontFamily = value.Family;
        settingsStore.SubtitleFontFamily = value.Family ?? string.Empty;
    }

    private async Task LoadSubtitleFontsAsync()
    {
        IReadOnlyList<string> families;
        try
        {
            families = await Task.Run(AssSubtitleRenderer.GetAvailableFontFamilies);
        }
        catch (Exception exception)
        {
            AppLog.Warning("Video.Subtitles", "Could not list subtitle fonts.", exception);
            return;
        }

        string? current = PlainTextSubtitleFontFamily;
        var options = new List<SubtitleFontOption>(families.Count + 2) { new(null) };
        // A saved family that is no longer installed stays selectable; the renderer falls back for it.
        if (current is not null && !families.Contains(current, StringComparer.OrdinalIgnoreCase))
            options.Add(new(current));
        options.AddRange(families.Select(family => new SubtitleFontOption(family)));

        SubtitleFontOptions = options;
        SelectedSubtitleFontOption = options.First(option =>
            string.Equals(option.Family, current, StringComparison.OrdinalIgnoreCase));
    }

    partial void OnSelectedShaderPackageChanged(VideoShaderPackageOption value)
    {
        if (!isShaderPackageLoaded)
            return;

        ShaderPackageOverride = value.Value;
        SaveShaderPackagePreference();
    }

    partial void OnSelectedSubtitleOptionChanged(SubtitleOption? value)
    {
        if (!suppressTrackSync)
        {
            SelectedSubtitleTrack = value?.Track;
        }
    }

    partial void OnSelectedSubtitleBrightnessOptionChanged(SubtitleBrightnessOption value) =>
        SaveSubtitleBrightnessPreference();

    partial void OnSelectedAudioTrackChanged(AudioTrack? value)
    {
        if (suppressTrackSync)
        {
            return;
        }

        if (value is not null)
        {
            Player.SelectAudioTrack(value);
        }

        SaveAudioTrackPreference();
    }

    partial void OnSelectedSubtitleTrackChanged(SubtitleTrack? value)
    {
        SyncSubtitleOption();

        if (CanSetSubtitleFont && !subtitleFontsRequested)
        {
            subtitleFontsRequested = true;
            _ = LoadSubtitleFontsAsync();
        }

        if (suppressTrackSync)
        {
            return;
        }

        Player.SelectSubtitleTrack(value);

        if (!restoringTracks && value is not null)
        {
            preferredSubtitleName = value.DisplayName;
        }

        SaveSubtitleTrackPreference();
    }

    private void SyncSubtitleOption()
    {
        var wasSuppressed = suppressTrackSync;
        suppressTrackSync = true;
        try
        {
            SelectedSubtitleOption = SubtitleOptions
                .FirstOrDefault(option => ReferenceEquals(option.Track, SelectedSubtitleTrack));
        }
        finally
        {
            suppressTrackSync = wasSuppressed;
        }
    }

    partial void OnSelectedChapterChanged(MediaChapter? value)
    {
        if (!suppressTrackSync && value is not null)
        {
            Player.Seek(value.Start);
        }
    }

    private void Stop()
    {
        Player.Stop();
        IsPlaying = false;
        PositionSeconds = 0;
    }

    private void ToggleMute()
    {
        if (IsMuted)
        {
            Volume = volumeBeforeMute <= 0.0001d ? 0.8d : volumeBeforeMute;
            return;
        }

        volumeBeforeMute = Volume;
        Volume = 0d;
    }

    private void CycleAudioTrack()
    {
        if (AudioTracks.Count < 2)
        {
            return;
        }

        var index = SelectedAudioTrack is { } current ? IndexOf(AudioTracks, current) : -1;
        SelectedAudioTrack = AudioTracks[(index + 1) % AudioTracks.Count];
    }

    /// <remarks>The picker's first entry turns subtitles off, so cycling passes through that too.</remarks>
    private void CycleSubtitleTrack()
    {
        if (SubtitleOptions.Count == 0)
        {
            return;
        }

        var index = SelectedSubtitleOption is { } current ? IndexOf(SubtitleOptions, current) : -1;
        SelectedSubtitleOption = SubtitleOptions[(index + 1) % SubtitleOptions.Count];
    }

    private static int IndexOf<T>(IReadOnlyList<T> items, T value)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (ReferenceEquals(items[i], value))
            {
                return i;
            }
        }

        return -1;
    }

    private void StepChapter(int delta)
    {
        if (Chapters.Count == 0)
        {
            return;
        }

        var chapter = Player.CurrentChapter ?? SelectedChapter;
        if (delta < 0 && chapter is not null && Player.Position - chapter.Start >= TimeSpan.FromSeconds(3))
        {
            Player.Seek(chapter.Start);
            return;
        }

        var index = chapter?.Index ?? 0;
        SelectedChapter = Chapters[Math.Clamp(index + delta, 0, Chapters.Count - 1)];
    }

    /// <summary>Turns subtitles off and back on without losing which track was showing.</summary>
    private void ToggleSubtitles()
    {
        if (SelectedSubtitleTrack is not null)
        {
            hiddenSubtitleTrack = SelectedSubtitleTrack;
            SelectedSubtitleTrack = null;
            return;
        }

        SelectedSubtitleTrack = hiddenSubtitleTrack ?? SubtitleTracks.FirstOrDefault();
    }

    /// <summary>
    /// Asks the platform to match the screen orientation to the picture: landscape for a wider
    /// than tall video, portrait otherwise. Only while full screen, and released when leaving it.
    /// </summary>
    private void UpdatePreferredOrientation()
    {
        if (!IsFullScreen || Player.Info?.DefaultVideoStream is not { } video || video.Height <= 0)
        {
            setPreferredOrientation(0);
            return;
        }

        setPreferredOrientation(video.DisplaySize.Width / video.DisplaySize.Height);
    }

    private void RefreshTracks()
    {
        suppressTrackSync = true;
        try
        {
            AudioTracks = Player.AudioTracks;
            SubtitleTracks = Player.SubtitleTracks;
            SubtitleOptions = [new SubtitleOption(null), .. Player.SubtitleTracks.Select(track => new SubtitleOption(track))];
            Chapters = Player.Chapters;
            SelectedAudioTrack = Player.ActiveAudioTrack;
            SelectedSubtitleTrack = Player.ActiveSubtitleTrack;
            SyncSubtitleOption();
        }
        finally
        {
            suppressTrackSync = false;
        }

        OnPropertyChanged(nameof(HasSubtitles));
        OnPropertyChanged(nameof(HasChapters));
        OnPropertyChanged(nameof(HasMultipleAudioTracks));
        PreviousChapterCommand.NotifyCanExecuteChanged();
        NextChapterCommand.NotifyCanExecuteChanged();
        CycleAudioTrackCommand.NotifyCanExecuteChanged();
        CycleSubtitleTrackCommand.NotifyCanExecuteChanged();
    }

    private void RefreshVideoRenditions()
    {
        VideoRendition? active = Player.ActiveVideoRendition;
        var qualities = Player.VideoRenditions
            .GroupBy(rendition => (rendition.Width, rendition.Height, rendition.Bandwidth))
            .Select(group => new VideoQualityOption(group.ToList()))
            .OrderByDescending(option => option.Renditions[0].Height)
            .ThenByDescending(option => option.Renditions[0].Bandwidth)
            .ToList();
        var angles = Player.VideoRenditions
            .Where(rendition => !string.IsNullOrWhiteSpace(rendition.Name))
            .Select(rendition => rendition.Name)
            .Distinct(StringComparer.Ordinal)
            .Select(name => new VideoAngleOption(name))
            .ToList();

        isVideoRenditionLoaded = false;
        VideoQualityOptions = qualities;
        CameraAngleOptions = angles;
        SelectedVideoQualityOption = active is null
            ? qualities.FirstOrDefault()
            : qualities.FirstOrDefault(option =>
                option.Renditions.Any(rendition => rendition.StreamIndex == active.StreamIndex));
        SelectedCameraAngleOption = active is null || string.IsNullOrWhiteSpace(active.Name)
            ? angles.FirstOrDefault()
            : angles.FirstOrDefault(option => string.Equals(option.Name, active.Name, StringComparison.Ordinal));
        isVideoRenditionLoaded = true;
    }

    private void SelectVideoRendition(IReadOnlyList<VideoRendition> candidates, string? angleName)
    {
        VideoRendition? target = !string.IsNullOrWhiteSpace(angleName)
            ? candidates.FirstOrDefault(rendition =>
                string.Equals(rendition.Name, angleName, StringComparison.Ordinal))
            : null;
        target ??= candidates.FirstOrDefault(rendition => rendition.IsDefault);
        target ??= candidates.FirstOrDefault();
        if (target is not null)
            Player.SelectVideoRendition(target);
    }

    private void OnStateChanged(object? sender, PlaybackState state) => Post(() => UpdateIsPlaying(state));

    /// <summary>
    /// Maps an engine state onto the transport button. Opening and seeking say nothing about what
    /// the user asked for, so the button is left as it was rather than flickering through them.
    /// </summary>
    private void UpdateIsPlaying(PlaybackState state)
    {
        if (state is PlaybackState.Opening or PlaybackState.Seeking)
        {
            return;
        }

        IsPlaying = state is PlaybackState.Playing or PlaybackState.Buffering;
    }

    private void OnStallChanged(object? sender, PlaybackStallReason reason) =>
        Post(() => IsBuffering = reason != PlaybackStallReason.None);

    private void OnErrorOccurred(object? sender, string message)
    {
        if (Volatile.Read(ref streamOpenInProgress) == 0)
            Post(() => ErrorMessage = message);
    }

    private void OnEndReached(object? sender, EventArgs e) => Post(() =>
    {
        if (IsLiveStream && currentStreamUri is { } streamUri)
        {
            string protocol = streamUri.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase)
                ? "DASH"
                : "HLS";
            AppLog.Error(
                "Video.Playback",
                $"Live {protocol} stream ended unexpectedly: {streamUri.AbsoluteUri}.");
            IsBuffering = false;
            ErrorMessage = string.Format(
                CultureInfo.CurrentCulture,
                Strings.Resources.VideoPlayer_LiveStreamEndedFormat,
                protocol);
            return;
        }

        SavePlaybackPosition(reachedEnd: true);
        PlayNextInList();
    });

    private void OnAudioTracksChanged(object? sender, EventArgs e) => Post(RefreshTracks);

    private void OnAudioTrackChanged(object? sender, AudioTrack? track) => Post(() =>
    {
        suppressTrackSync = true;
        try
        {
            SelectedAudioTrack = Player.ActiveAudioTrack;
        }
        finally
        {
            suppressTrackSync = false;
        }
    });

    private void OnVideoRenditionsChanged(object? sender, EventArgs e) => Post(RefreshVideoRenditions);

    private void OnVideoRenditionChanged(object? sender, VideoRendition? rendition) => Post(() =>
    {
        RefreshVideoRenditions();
        UpdatePictureAspectRatio();
        UpdatePreferredOrientation();
        OfferPictureInPicture();
    });

    private void OnStreamingTimelineChanged(object? sender, EventArgs e) => Post(() =>
    {
        if (currentStreamProvider is { } provider)
            ApplyStreamingTimeline(provider.Timeline);
    });

    private void OnStreamingLoadStageChanged(object? sender, EventArgs e)
    {
        if (sender is not IStreamingMediaSourceProvider provider)
            return;

        Post(() =>
        {
            if (IsStreamLoading && ReferenceEquals(provider, currentStreamProvider))
                UpdateStreamingLoadStage(provider.LoadStage);
        });
    }

    private void UpdateStreamingLoadStage(StreamingLoadStage stage) => StreamLoadingText = stage switch
    {
        StreamingLoadStage.Connecting => Strings.Resources.VideoPlayer_StreamConnecting,
        StreamingLoadStage.LoadingManifest => Strings.Resources.VideoPlayer_StreamLoadingManifest,
        StreamingLoadStage.ReadingStreamInformation => Strings.Resources.VideoPlayer_StreamReadingInformation,
        _ => Strings.Resources.Common_Loading,
    };

    private void ApplyStreamingTimeline(StreamingTimeline timeline)
    {
        if (!timeline.IsLive)
        {
            ResetStreamingTimeline();
            return;
        }

        IsLiveStream = true;
        LiveWindowStartSeconds = timeline.StartSeconds;
        LiveWindowEndSeconds = timeline.EndSeconds;
        LiveTargetDurationSeconds = timeline.TargetDurationSeconds;
        CanSeekBackWithinLiveWindow = timeline.CanSeekBack;
        OnPropertyChanged(nameof(CanSeek));
        PushSystemMediaProgress();
    }

    private void ResetStreamingTimeline()
    {
        IsLiveStream = false;
        LiveWindowStartSeconds = 0;
        LiveWindowEndSeconds = 0;
        LiveTargetDurationSeconds = 0;
        CanSeekBackWithinLiveWindow = false;
    }

    private void DetachStreamProvider()
    {
        if (currentStreamProvider is { } provider)
        {
            provider.TimelineChanged -= OnStreamingTimelineChanged;
            provider.LoadStageChanged -= OnStreamingLoadStageChanged;
        }
        currentStreamProvider = null;
        IsStreamLoading = false;
        StreamLoadingText = string.Empty;
    }

    private void OnSubtitleTracksChanged(object? sender, EventArgs e) => Post(RefreshTracks);

    private void OnSubtitleTrackChanged(object? sender, SubtitleTrack? track) => Post(() =>
    {
        suppressTrackSync = true;
        try
        {
            SelectedSubtitleTrack = Player.ActiveSubtitleTrack;
        }
        finally
        {
            suppressTrackSync = false;
        }
    });

    private void OnChaptersChanged(object? sender, EventArgs e) => Post(RefreshTracks);

    private void PlayNextInList()
    {
        IsPlaying = false;
        if (!IsAutoPlayNextEnabled)
        {
            return;
        }

        StepPlaylist(1);
    }

    /// <summary>Moves through the video list or restarts the current video when stepping back.</summary>
    private void StepPlaylist(int delta)
    {
        if (currentVideo is null)
        {
            return;
        }

        if (delta < 0 && CanSeek && Player.Position.TotalSeconds - SeekMinimumSeconds >= 3)
        {
            Seek(SeekMinimumSeconds);
            return;
        }

        var index = Playlist
            .Select((entry, position) => (entry, position))
            .FirstOrDefault(pair => string.Equals(pair.entry.Video.Path, currentVideo.Path, StringComparison.Ordinal))
            .position;
        var next = index + delta;
        if (next >= 0 && next < Playlist.Count)
        {
            PlayVideoCommand.Execute(Playlist[next].Video);
        }
    }

    private static void Post(Action action) => Avalonia.Threading.Dispatcher.UIThread.Post(action);

    private static string Format(TimeSpan value) => value.TotalHours >= 1
        ? value.ToString(@"h\:mm\:ss")
        : value.ToString(@"m\:ss");
}
