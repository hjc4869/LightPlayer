using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using LightMediaRenderer.Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Media;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Services.PhotoExport;
using LightMediaRenderer.Avalonia.Shaders;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.IO;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// Actions the page can only carry out through the control or the window showing it, and so asks
/// the view for rather than doing itself.
/// </summary>
public enum PhotoViewerAction
{
    ZoomIn,
    ZoomOut,
    ZoomToActualSize,
    ZoomToFit,
    CopyImage,
    CopyPath,
    StepPrevious,
    StepNext,
}

/// <summary>
/// Shows one still picture, decoded through the same path the video renderer draws frames from.
/// </summary>
/// <remarks>
/// Previews and full-resolution frames load asynchronously. Oriented source dimensions let the
/// control preserve the picture's geometry as a preview gives way to the decoded frame.
/// </remarks>
public sealed partial class PhotoViewerPageViewModel : PageViewModelBase, INavigationParameterSource, IInPageBackHandler,
    IDisposable, IAsyncDisposable
{
    private readonly IClientMediaResolver mediaResolver;
    private readonly PhotoViewerCallbacks callbacks;
    private readonly IReadOnlyList<MediaReference> photos;
    private readonly CancellationTokenSource cancellation = new();
    private readonly HashSet<VideoTileModel> originPreviews = [];
    private readonly HashSet<Task> pendingLoads = [];
    private readonly TaskCompletionSource viewportReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Size previewViewport;

    /// <summary>Cancels the picture being decoded when another is paged to before it arrives.</summary>
    private CancellationTokenSource load;

    /// <summary>Cancels the pictures being read ahead when the ones either side change.</summary>
    private CancellationTokenSource readAhead;
    private Task loading = Task.CompletedTask;
    private Task currentPreviewLoading = Task.CompletedTask;
    private Task previousPreviewLoading = Task.CompletedTask;
    private Task nextPreviewLoading = Task.CompletedTask;
    private Task imageReleased = Task.CompletedTask;
    private Task? disposal;
    private bool disposed;
    private int index;
    private bool isPreviewSelecting;
    private bool isSwiping;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    [NotifyPropertyChangedFor(nameof(HasGainMap))]
    [NotifyPropertyChangedFor(nameof(HasAbsoluteLuminance))]
    private VideoFrame? image;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGainMap))]
    [NotifyPropertyChangedFor(nameof(HasAbsoluteLuminance))]
    private PhotoPreview? preview;

    [ObservableProperty]
    private PhotoPreview? previousPreview;

    [ObservableProperty]
    private PhotoPreview? nextPreview;

    [ObservableProperty]
    private Size naturalSize;

    [ObservableProperty]
    private Size previousNaturalSize;

    [ObservableProperty]
    private Size nextNaturalSize;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleRawRenderingCommand))]
    private bool isLoading = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleRawRenderingCommand))]
    private bool canSwitchRawRendering;

    [ObservableProperty]
    private bool isRawRendering;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailed))]
    private string? failure;

    [ObservableProperty]
    private bool enableHdrPassthrough;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayedGainMap))]
    private bool enableGainMap = true;

    [ObservableProperty]
    private int displayPanelBitDepth;

    /// <summary>
    /// Clockwise turn the user has asked for, in degrees. Never written back to the file: this is
    /// how the picture is being looked at, not how it is stored.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Description))]
    private int rotation;

    /// <summary>
    /// Whether the viewer controls are visible.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPreviousButton))]
    [NotifyPropertyChangedFor(nameof(ShowNextButton))]
    private bool areControlsVisible = true;

    [ObservableProperty]
    private bool isFullScreen;

    // The photo hides the shell's own chrome, so hosts without a native back affordance keep this
    // button in both wide and narrow layouts.
    [ObservableProperty]
    private bool showBackButton = !PlatformServices.HostProvidesBackNavigation;

    public PhotoViewerPageViewModel(
        IAppSettingsStore settingsStore,
        IClientMediaResolver mediaResolver,
        PhotoViewerRequest request,
        PhotoViewerCallbacks callbacks)
    {
        this.mediaResolver = mediaResolver;
        ToneProfiles = new PhotoToneMappingViewModel(settingsStore);
        SaveAs = new PhotoSaveAsViewModel(mediaResolver, settingsStore, cancellation.Token);
        ToneProfiles.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is null or "" or nameof(PhotoToneMappingViewModel.Options))
                OnPropertyChanged(nameof(DisplayedToneMapping));
        };
        SaveAs.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is null or "" or nameof(PhotoSaveAsViewModel.IsOpen))
                OnPropertyChanged(nameof(CanHandleBack));
            if (args.PropertyName is null or "" or nameof(PhotoSaveAsViewModel.OverridesToneMapping) or
                nameof(PhotoSaveAsViewModel.PreviewToneMapping) or nameof(PhotoSaveAsViewModel.HidesGainMap))
            {
                OnPropertyChanged(nameof(DisplayedToneMapping));
                OnPropertyChanged(nameof(DisplayedGainMap));
            }
        };
        this.callbacks = callbacks;
        photos = request.Photos.Count > 0 ? request.Photos : [MediaReference.Local(string.Empty)];
        index = Math.Clamp(request.Index, 0, photos.Count - 1);
        load = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        readAhead = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        Close = callbacks.Close;
        BackCommand = new RelayCommand(callbacks.Close);
        TitleKey = nameof(Strings.Resources.Nav_PhotoViewer);

        // The step itself is left to the view: it slides the pictures across first, and the page
        // only changes once that has landed.
        PreviousCommand = new RelayCommand(() => Request(PhotoViewerAction.StepPrevious), () => HasPrevious);
        NextCommand = new RelayCommand(() => Request(PhotoViewerAction.StepNext), () => HasNext);
        ZoomInCommand = new RelayCommand(() => Request(PhotoViewerAction.ZoomIn));
        ZoomOutCommand = new RelayCommand(() => Request(PhotoViewerAction.ZoomOut));
        ZoomToActualSizeCommand = new RelayCommand(() => Request(PhotoViewerAction.ZoomToActualSize));
        ZoomToFitCommand = new RelayCommand(() => Request(PhotoViewerAction.ZoomToFit));
        CopyImageCommand = new RelayCommand(() => Request(PhotoViewerAction.CopyImage), () => HasImage);
        CopyPathCommand = new RelayCommand(() => Request(PhotoViewerAction.CopyPath));
        SaveAsCommand = new RelayCommand(ToggleSaveAs, () => Image is { PhotoAnimation: null });
        RotateCommand = new RelayCommand<string>(Rotate);
        OpenContainingFolderCommand = new RelayCommand(OpenContainingFolder, () => CanRevealInFileManager);
        OpenInNewWindowCommand = new RelayCommand(OpenInNewWindow, () => CanOpenInNewWindow);
        OpenWithCommand = new AsyncRelayCommand(OpenWithAsync, () => CanOpenWith);
        ShareCommand = new AsyncRelayCommand(ShareAsync, () => CanShare);
        PropertiesCommand = new RelayCommand(ShowProperties, () => callbacks.ShowProperties is not null);
        ToggleFullScreenCommand = new RelayCommand(() => IsFullScreen = !IsFullScreen, () => callbacks.SetFullScreen is not null);
        ToggleRawRenderingCommand = new RelayCommand(() =>
        {
            if (!isSwiping && !isPreviewSelecting)
                StartLoad(!IsRawRendering);
        }, () => CanSwitchRawRendering && !IsLoading);

        // An Android content URI names nothing, so the real name is read from the platform below.
        FileName = System.IO.Path.GetFileName(Current.Path);

        enableHdrPassthrough = settingsStore.EnableHdrPassthrough;
        displayPanelBitDepth = settingsStore.DisplayPanelBitDepth;

        StartLoad();
    }

    /// <summary>Raised for the actions only the view can carry out; handled on the UI thread.</summary>
    public event Action<PhotoViewerAction>? ActionRequested;

    /// <summary>The file being shown.</summary>
    public string Path => Current.Path;

    /// <summary>Reopens the page on the photo being shown now.</summary>
    public object NavigationParameter => Current;

    public string FileName { get; private set; }

    /// <summary>Leaves the page, which is how the shell takes it off the navigation stack.</summary>
    public Action Close { get; }

    public IRelayCommand BackCommand { get; }

    public IRelayCommand PreviousCommand { get; }

    public IRelayCommand NextCommand { get; }

    public IRelayCommand ZoomInCommand { get; }

    public IRelayCommand ZoomOutCommand { get; }

    public IRelayCommand ZoomToActualSizeCommand { get; }

    public IRelayCommand ZoomToFitCommand { get; }

    public IRelayCommand CopyImageCommand { get; }

    public IRelayCommand CopyPathCommand { get; }

    /// <summary>Opens the panel that saves the picture in another format or size, or closes it.</summary>
    public IRelayCommand SaveAsCommand { get; }

    public IRelayCommand<string> RotateCommand { get; }

    public IRelayCommand OpenContainingFolderCommand { get; }

    public IRelayCommand OpenInNewWindowCommand { get; }

    public IRelayCommand OpenWithCommand { get; }

    public IRelayCommand ShareCommand { get; }

    public IRelayCommand PropertiesCommand { get; }

    public IRelayCommand ToggleFullScreenCommand { get; }

    public IRelayCommand ToggleRawRenderingCommand { get; }

    /// <summary>Tone mapping for pictures with absolute luminance.</summary>
    public PhotoToneMappingViewModel ToneProfiles { get; }

    public PhotoSaveAsViewModel SaveAs { get; }

    /// <summary>The tone mapping the picture is drawn with, previewing the saved file while the save panel asks to.</summary>
    public VideoSdrToneMappingOptions? DisplayedToneMapping =>
        SaveAs.OverridesToneMapping ? SaveAs.PreviewToneMapping : ToneProfiles.Options;

    public bool DisplayedGainMap => EnableGainMap && !SaveAs.HidesGainMap;

    /// <summary>A back gesture closes the save panel before it leaves the page.</summary>
    public bool CanHandleBack => SaveAs.IsOpen;

    public bool TryHandleBack()
    {
        if (!SaveAs.IsOpen)
            return false;
        SaveAs.Close();
        return true;
    }

    partial void OnIsFullScreenChanged(bool value) => callbacks.SetFullScreen?.Invoke(value);

    public bool HasImage => Image is not null;

    public bool HasGainMap => Image?.GainMap is not null ||
        Image is null && Preview is { IsViewportPreview: true, Image: PhotoFrameImage { Frame.GainMap: not null } };

    public bool HasAbsoluteLuminance => (Image ?? (Preview is { IsViewportPreview: true, Image: PhotoFrameImage photo }
        ? photo.Frame : null)) is { GainMap: null, ColorTransfer: VideoColorTransfer.Pq };

    public bool HasFailed => Failure is not null;

    public bool HasPrevious => index > 0;

    public bool HasNext => index < photos.Count - 1;

    /// <summary>The edge buttons are part of the chrome, and only offered where they lead somewhere.</summary>
    public bool ShowPreviousButton => AreControlsVisible && HasPrevious;

    public bool ShowNextButton => AreControlsVisible && HasNext;

    public bool CanRevealInFileManager =>
        callbacks.RevealInFileManager is not null &&
        mediaResolver.GetCapabilities(Current.SourceId).SupportsFileReveal;

    public bool CanShare => PlatformServices.FileShareService?.CanShare == true;

    public bool CanOpenWith => Current.SourceId == LibrarySourceId.Local;

    public bool CanOpenInNewWindow => Current.SourceId == LibrarySourceId.Local && !string.IsNullOrEmpty(Current.Path) &&
        callbacks.TryOpenInNewWindow is not null;

    /// <summary>
    /// The size the picture is seen at, which for one a camera recorded sideways -- or one the user
    /// has turned -- is not the size it was stored at.
    /// </summary>
    public string Description
    {
        get
        {
            if (Image is not { } frame)
                return string.Empty;

            (int width, int height) = (frame.Rotation + Rotation) % 180 == 90
                ? (frame.Height, frame.Width)
                : (frame.Width, frame.Height);
            return $"{width} × {height}";
        }
    }

    /// <summary>
    /// The picture as pixels a clipboard can take, decoded again at full size because the frame on
    /// screen is whatever layout the codec produced and may be neither packed nor eight bits deep.
    /// </summary>
    public async Task<Bitmap?> RenderBitmapAsync()
    {
        Task<Bitmap?> operation = RenderBitmapCoreAsync();
        await TrackLoadAsync(operation).ConfigureAwait(true);
        return await operation.ConfigureAwait(true);
    }

    private async Task<Bitmap?> RenderBitmapCoreAsync()
    {
        if (Image is not { } frame)
            return null;

        MediaReference current = Current;
        int longestEdge = Math.Max(frame.Width, frame.Height);
        int turn = Rotation;
        bool? renderRaw = CanSwitchRawRendering ? IsRawRendering : null;
        CancellationToken token = cancellation.Token;

        IMultiMediaPlatformIO platform = await mediaResolver
            .ResolvePlatformAsync(current, token)
            .ConfigureAwait(true);

        StillImageThumbnail? decoded = await ImageDecoding.DecodePhotoThumbnailAsync(
            new PhotoDecodeRequest(platform, current.Path) { RenderRaw = renderRaw }, longestEdge, token).ConfigureAwait(true);

        return decoded is null ? null : PhotoBitmap.Create(decoded, turn);
    }

    internal MediaReference Current => photos[index];

    internal int PhotoCount => photos.Count;

    internal int SelectedIndex => index;

    internal string GetPhotoName(int target) => System.IO.Path.GetFileName(photos[target].Path);

    internal PhotoPreview? RetainPreview(int target) => ((target - index) switch
    {
        -1 => PreviousPreview,
        0 => Preview,
        1 => NextPreview,
        _ => null,
    })?.AddRef();

    internal async Task LoadThumbnailAsync(int target, CancellationToken token, Action<PhotoPreview> fill)
    {
        if (cancellation.IsCancellationRequested)
            return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, token);
        Task? viewerLoad = (target - index) switch
        {
            -1 => previousPreviewLoading,
            0 => currentPreviewLoading,
            1 => nextPreviewLoading,
            _ => null,
        };
        if (viewerLoad is not null)
        {
            await viewerLoad.WaitAsync(linked.Token).ConfigureAwait(true);
            linked.Token.ThrowIfCancellationRequested();
            if (RetainPreview(target) is { } retained)
            {
                fill(retained);
                return;
            }
        }
        await LoadPreviewAsync(photos[target], linked.Token, fill).ConfigureAwait(true);
    }

    internal bool IsTransitioning { get; set; }

    internal IScrollPositionPageViewModel? OriginPage { get; set; }

    internal double OriginScrollOffset { get; set; }

    internal void SupplyPreview(PhotoPreview value)
    {
        if (!cancellation.IsCancellationRequested && (Preview is null ||
            value.IsViewportPreview && !Preview.IsViewportPreview))
            Preview = value;
        else
            value.Release();
    }

    private void Request(PhotoViewerAction action) => ActionRequested?.Invoke(action);

    private void OpenInNewWindow()
    {
        if (CanOpenInNewWindow && callbacks.TryOpenInNewWindow?.Invoke(Current.Path) == true)
            callbacks.Close();
    }

    /// <summary>
    /// Steps to the picture <paramref name="offset"/> places along the list that was opened.
    /// </summary>
    public void Step(int offset) => SelectPhoto(index + offset);

    internal void BeginSwipe()
    {
        if (isSwiping || cancellation.IsCancellationRequested)
            return;

        isSwiping = true;
        load.Cancel();
        load.Dispose();
        load = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        IsLoading = false;
    }

    internal void EndSwipe()
    {
        if (!isSwiping || cancellation.IsCancellationRequested)
            return;

        isSwiping = false;
        if (Image is null && !isPreviewSelecting)
            StartLoad();
    }

    internal void BeginPreviewSelection()
    {
        if (isPreviewSelecting || cancellation.IsCancellationRequested)
            return;

        isPreviewSelecting = true;
        isSwiping = false;
        load.Cancel();
        load.Dispose();
        load = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        readAhead.Cancel();
        readAhead.Dispose();
        readAhead = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        Image = null;
        CopyImageCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(Description));
        StartLoad();
    }

    internal void EndPreviewSelection()
    {
        if (!isPreviewSelecting || cancellation.IsCancellationRequested)
            return;

        isPreviewSelecting = false;
        StartLoad();
    }

    internal void SelectPhoto(int target, PhotoPreview? selectedPreview = null)
    {
        if (target < 0 || target >= photos.Count || cancellation.IsCancellationRequested)
        {
            return;
        }

        if (target == index)
        {
            if (selectedPreview is not null)
                SupplyPreview(selectedPreview.AddRef());
            return;
        }

        int offset = target - index;
        bool adjacent = Math.Abs(offset) == 1;
        load.Cancel();
        load.Dispose();
        load = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        readAhead.Cancel();
        readAhead.Dispose();
        readAhead = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);

        PhotoPreview? outgoingPreview = adjacent ? Preview?.AddRef() : null;
        PhotoPreview? incomingPreview = selectedPreview?.AddRef() ?? (adjacent
            ? (offset > 0 ? NextPreview : PreviousPreview)?.AddRef() : null);
        Size outgoingSize = adjacent ? NaturalSize : default;
        Size incomingSize = adjacent ? (offset > 0 ? NextNaturalSize : PreviousNaturalSize)
            : incomingPreview?.NaturalSize ?? default;

        index = target;
        ToneProfiles.Revert();
        Rotation = 0;
        CanSwitchRawRendering = false;
        IsRawRendering = false;
        Failure = null;
        NaturalSize = incomingSize;
        Preview = incomingPreview;
        Image = null;

        if (offset > 0)
        {
            PreviousPreview = outgoingPreview;
            NextPreview = null;
            PreviousNaturalSize = outgoingSize;
            NextNaturalSize = default;
        }
        else
        {
            NextPreview = outgoingPreview;
            PreviousPreview = null;
            NextNaturalSize = outgoingSize;
            PreviousNaturalSize = default;
        }

        FileName = System.IO.Path.GetFileName(Current.Path);
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(Path));
        OnPropertyChanged(nameof(NavigationParameter));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(SelectedIndex));
        RefreshCommands();

        StartLoad();
    }

    private void Rotate(string? degrees)
    {
        if (int.TryParse(degrees, out int turn))
        {
            Rotation = (Rotation + turn) % 360;
        }
    }

    partial void OnRotationChanged(int value) => SaveAs.Rotate(value);

    private void ToggleSaveAs()
    {
        if (SaveAs.IsOpen)
        {
            SaveAs.Close();
            return;
        }

        if (Image is not { PhotoAnimation: null } frame)
            return;
        Size size = DisplaySize(frame);
        SaveAs.Open(new PhotoSaveSource(
            Current,
            FileName,
            CanSwitchRawRendering ? IsRawRendering : null,
            PhotoExportSource.Classify(frame),
            frame.HasAlpha,
            size.Width,
            size.Height,
            Rotation,
            ToneMappingProfileChoices.ProfileName(ToneProfiles.SelectedProfile),
            ToneProfiles.Options,
            PhotoExportColor.SourceGamuts(frame)));
    }

    private void OpenContainingFolder() => callbacks.RevealInFileManager?.Invoke(Current.Path);

    private void ShowProperties() => callbacks.ShowProperties?.Invoke(Current, FileName);

    private async Task OpenWithAsync()
    {
        if (!CanOpenWith || PlatformServices.FileShareService is not { } service)
        {
            return;
        }

        string path = Current.Path;
        string mediaType = MediaContainerFormats.ImageMediaType(FileName);
        await AppErrorReporter.GuardAsync(() => service.OpenWithAsync(path, mediaType)).ConfigureAwait(true);
    }

    private async Task ShareAsync()
    {
        if (PlatformServices.FileShareService is not { } service)
        {
            return;
        }

        MediaReference current = Current;
        string mediaType = MediaContainerFormats.ImageMediaType(FileName);
        await AppErrorReporter.GuardAsync(async () =>
        {
            string path = await LocalMediaFile
                .EnsureAsync(current, mediaResolver, cancellation.Token)
                .ConfigureAwait(true);

            await service.ShareAsync(path, mediaType).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(HasPrevious));
        OnPropertyChanged(nameof(HasNext));
        OnPropertyChanged(nameof(ShowPreviousButton));
        OnPropertyChanged(nameof(ShowNextButton));
        OnPropertyChanged(nameof(CanRevealInFileManager));
        OnPropertyChanged(nameof(CanOpenWith));
        OnPropertyChanged(nameof(CanOpenInNewWindow));
        PreviousCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
        CopyImageCommand.NotifyCanExecuteChanged();
        OpenContainingFolderCommand.NotifyCanExecuteChanged();
        OpenWithCommand.NotifyCanExecuteChanged();
        OpenInNewWindowCommand.NotifyCanExecuteChanged();
    }

    private void StartLoad(bool? renderRaw = null) => loading = LoadAsync(loading, renderRaw ?? IsRawRendering);

    internal void SetPreviewViewport(Size pixels)
    {
        if (pixels.Width <= 0 || pixels.Height <= 0 || !double.IsFinite(pixels.Width) || !double.IsFinite(pixels.Height))
            return;
        previewViewport = pixels;
        viewportReady.TrySetResult();
    }

    private async Task LoadAsync(Task previous, bool renderRaw)
    {
        CancellationToken token = load.Token;
        MediaReference current = Current;
        bool decodeFullImage = !isPreviewSelecting && !isSwiping;
        Failure = null;
        IsLoading = decodeFullImage;
        var viewportPreviewTried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        currentPreviewLoading = LoadViewerPreviewAsync(current, Preview, token, SupplyPreview,
            size => NaturalSize = size, viewportPreviewTried);
        previousPreviewLoading = HasPrevious && PreviousPreview is null
            ? LoadViewerPreviewAsync(photos[index - 1], PreviousPreview, readAhead.Token, value => PreviousPreview = value,
                size => PreviousNaturalSize = size)
            : Task.CompletedTask;
        nextPreviewLoading = HasNext && NextPreview is null
            ? LoadViewerPreviewAsync(photos[index + 1], NextPreview, readAhead.Token, value => NextPreview = value,
                size => NextNaturalSize = size)
            : Task.CompletedTask;
        try
        {
            await previous.ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            if (!decodeFullImage)
                return;
            await imageReleased.WaitAsync(token).ConfigureAwait(true);
            await RefreshNameAsync(current, token).ConfigureAwait(true);
            // A library thumbnail still being generated would hold the photo up for as long as its decode takes.
            await viewportPreviewTried.Task.ConfigureAwait(true);
            token.ThrowIfCancellationRequested();

            VideoFrame? decoded = await DecodeAsync(current, renderRaw, token, size => NaturalSize = size).ConfigureAwait(true);
            if (token.IsCancellationRequested)
            {
                Task? released = decoded?.PhotoSurface?.WhenReleased;
                decoded?.Release();
                if (released is not null)
                    await released.ConfigureAwait(true);
                return;
            }

            if (decoded is null)
            {
                Failure = Localized(
                    nameof(Strings.Resources.Photo_LoadFailedDescription),
                    "The file may be damaged, or its format may not be supported by this build.");
                return;
            }

            NaturalSize = DisplaySize(decoded);
            Image = decoded;
            CanSwitchRawRendering |= decoded.IsEmbeddedRawPreview;
            IsRawRendering = CanSwitchRawRendering && !decoded.IsEmbeddedRawPreview;
            OnPropertyChanged(nameof(Description));
            CopyImageCommand.NotifyCanExecuteChanged();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Failure = AppErrorReporter.LogAndGetMessage("Photo", $"Could not open {FileName}.", ex);
        }
        finally
        {
            if (decodeFullImage && !token.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    private Task LoadPreviewAsync(MediaReference media, CancellationToken token, Action<PhotoPreview> fill) =>
        TrackLoadAsync(LoadPreviewCoreAsync(media, token, fill));

    private Task LoadViewerPreviewAsync(MediaReference media, PhotoPreview? existing, CancellationToken token,
        Action<PhotoPreview> fill, Action<Size> setSize, TaskCompletionSource? viewportPreviewTried = null) =>
        TrackLoadAsync(LoadViewerPreviewCoreAsync(media, existing?.AddRef(), token, fill, setSize, viewportPreviewTried));

    /// <param name="viewportPreviewTried">Completed once the viewport preview was tried, before any library thumbnail.</param>
    private async Task LoadViewerPreviewCoreAsync(MediaReference media, PhotoPreview? existing, CancellationToken token,
        Action<PhotoPreview> fill, Action<Size> setSize, TaskCompletionSource? viewportPreviewTried)
    {
        try
        {
            if (existing?.IsViewportPreview == true)
            {
                if (existing.NaturalSize is { Width: > 0, Height: > 0 } size)
                    setSize(size);
                return;
            }
            await viewportReady.Task.WaitAsync(token).ConfigureAwait(true);
            try
            {
                Size cached = existing?.Image is Bitmap bitmap
                    ? new Size(bitmap.PixelSize.Width, bitmap.PixelSize.Height) : existing?.Image.Size ?? default;
                var previewRequest = new PhotoPreviewRequest((int)Math.Ceiling(previewViewport.Width),
                    (int)Math.Ceiling(previewViewport.Height), (int)cached.Width, (int)cached.Height)
                {
                    ExistingMaxDimension = callbacks.LoadPreview is null ? 0 : callbacks.PreviewMaxDimension,
                };
                IMultiMediaPlatformIO platform = await mediaResolver.ResolvePlatformAsync(media, token).ConfigureAwait(true);
                Size naturalSize = default;
                VideoFrame? frame = await ImageDecoding.DecodePhotoAsync(
                    new PhotoDecodeRequest(platform, media.Path) { Preview = previewRequest }, new Progress<Size>(size =>
                    {
                        if (!token.IsCancellationRequested)
                        {
                            naturalSize = size;
                            setSize(size);
                        }
                    }), token).ConfigureAwait(true);
                if (frame is not null)
                {
                    PhotoFrameImage image;
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        image = new PhotoFrameImage(frame);
                    }
                    finally { frame.Release(); }
                    fill(new PhotoPreview(image, image.Dispose, isViewportPreview: true, naturalSize: naturalSize));
                    return;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                AppLog.Warning("Photo", $"Could not load embedded preview for {media.Path}.", exception);
            }
            viewportPreviewTried?.TrySetResult();
            if (existing is null)
                await LoadPreviewCoreAsync(media, token, fill).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            viewportPreviewTried?.TrySetResult();
            existing?.Release();
        }
    }

    private async Task LoadPreviewCoreAsync(MediaReference media, CancellationToken token, Action<PhotoPreview> fill)
    {
        if (callbacks.LoadPreview is not { } loader)
            return;
        try
        {
            IImage? thumbnail = await loader(media, token).ConfigureAwait(true);
            if (thumbnail is null)
                return;
            if (token.IsCancellationRequested)
                (thumbnail as IDisposable)?.Dispose();
            else
                fill(new PhotoPreview(thumbnail, () => (thumbnail as IDisposable)?.Dispose()));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AppLog.Warning("Photo", $"Could not load preview for {media.Path}.", exception);
        }
    }

    private async Task TrackLoadAsync(Task operation)
    {
        pendingLoads.Add(operation);
        try
        {
            await operation.ConfigureAwait(true);
        }
        finally
        {
            pendingLoads.Remove(operation);
        }
    }

    /// <summary>
    /// Opened through the platform rather than the filesystem: a picture handed to the app on
    /// Android is a content URI, and one in a remote library is not on this device at all.
    /// </summary>
    private async Task<VideoFrame?> DecodeAsync(MediaReference media, bool renderRaw, CancellationToken token, Action<Size> setSize)
    {
        IMultiMediaPlatformIO platform = await mediaResolver.ResolvePlatformAsync(media, token).ConfigureAwait(true);
        return await ImageDecoding.DecodePhotoAsync(new PhotoDecodeRequest(platform, media.Path) { RenderRaw = renderRaw }, new Progress<Size>(size =>
        {
            if (!token.IsCancellationRequested && Image is null)
                setSize(size);
        }), token).ConfigureAwait(true);
    }

    private static Size DisplaySize(VideoFrame frame)
    {
        double width = frame.Width * (frame.PixelAspectRatio > 0 ? frame.PixelAspectRatio : 1);
        return frame.Rotation is 90 or 270 ? new Size(frame.Height, width) : new Size(width, frame.Height);
    }

    /// <summary>
    /// The name the platform knows the file by. An Android content URI names nothing, and a remote
    /// library's path is its server's business, so the constructor's guess is corrected here.
    /// </summary>
    private async Task RefreshNameAsync(MediaReference media, CancellationToken token)
    {
        try
        {
            IMultiMediaPlatformIO platform = await mediaResolver
                .ResolvePlatformAsync(media, token)
                .ConfigureAwait(true);

            if (await platform.GetMediaFilePropertiesAsync(media.Path, token)
                    .ConfigureAwait(true) is { Name.Length: > 0 } properties &&
                !token.IsCancellationRequested)
            {
                FileName = properties.Name;
                OnPropertyChanged(nameof(FileName));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    partial void OnImageChanged(VideoFrame? oldValue, VideoFrame? newValue)
    {
        if (oldValue?.PhotoSurface is { } surface)
            imageReleased = surface.WhenReleased;
        oldValue?.Release();
        SaveAs.Close();
        SaveAsCommand.NotifyCanExecuteChanged();
    }

    partial void OnPreviewChanged(PhotoPreview? oldValue, PhotoPreview? newValue) => oldValue?.Release();

    partial void OnPreviousPreviewChanged(PhotoPreview? oldValue, PhotoPreview? newValue) => oldValue?.Release();

    partial void OnNextPreviewChanged(PhotoPreview? oldValue, PhotoPreview? newValue) => oldValue?.Release();

    internal void RetainOriginPreviews(IEnumerable<VideoTileModel> tiles)
    {
        foreach (var tile in tiles)
        {
            if (originPreviews.Add(tile))
                tile.RequestPreview();
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        foreach (var tile in originPreviews)
            tile.ReleasePreview();
        originPreviews.Clear();

        IsFullScreen = false;
        SaveAs.Close();
        cancellation.Cancel();
        load.Dispose();
        readAhead.Dispose();
        cancellation.Dispose();

        // Clearing the properties is what drops the page's references, through the handlers above.
        // Releasing them here as well would free the buffers out from under the control and under
        // any draw the render thread has not replayed yet.
        Image = null;
        Preview = null;
        PreviousPreview = null;
        NextPreview = null;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return new ValueTask(disposal ??= CompleteDisposalAsync());
    }

    private async Task CompleteDisposalAsync()
    {
        try
        {
            await Task.WhenAll(new List<Task>(pendingLoads) { loading, imageReleased }).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }
}
