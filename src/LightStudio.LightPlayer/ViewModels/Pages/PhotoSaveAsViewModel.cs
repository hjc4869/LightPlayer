using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.ImageEncoding;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Services.PhotoExport;
using LightStudio.Logging;
using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>One entry of a save option list, titled in the current language.</summary>
public sealed class PhotoSaveChoice(object value, Func<string> title) : ViewModelBase
{
    public object Value { get; } = value;

    public string Title => title();
}

/// <summary>The picture a save starts from.</summary>
/// <param name="Width">The width the picture is shown at before the user's turn.</param>
/// <param name="Height">The height the picture is shown at before the user's turn.</param>
/// <param name="ToneMapping">The tone mapping the viewer draws the picture with, saved or not.</param>
/// <param name="SourceGamuts">Matrices to BT.2020 from the linear spaces the picture's colors are in.</param>
internal sealed record PhotoSaveSource(
    MediaReference Media,
    string FileName,
    bool? RenderRaw,
    PhotoExportSourceKind Kind,
    bool HasAlpha,
    double Width,
    double Height,
    int Rotation,
    string? ProfileName,
    VideoSdrToneMappingOptions? ToneMapping,
    float[][] SourceGamuts);

/// <summary>
/// Chooses how the photo on screen is saved: format, encoder, layout, quality, size, and what
/// becomes of HDR content. Tone mapping changes made here are never stored in a profile.
/// </summary>
public sealed partial class PhotoSaveAsViewModel : ViewModelBase
{
    private const int MaximumSize = 65535;
    private const int TileStep = 64;

    /// <summary>The most tiles a HEIF grid holds along each axis.</summary>
    private const int MaximumTilesPerAxis = 256;

    private readonly IClientMediaResolver mediaResolver;
    private readonly IAppSettingsStore settingsStore;
    private readonly CancellationToken lifetime;
    private IReadOnlyList<StillImageEncoderInfo> encoders = [];
    private Task? encoderProbe;
    private PhotoSaveSource? source;
    private float[][] sourceGamuts = [];
    private CancellationTokenSource? saving;
    private PhotoExportFormat preferredFormat = PhotoExportFormat.Jpeg;
    private PhotoExportHdrEncoding preferredHdr = PhotoExportHdrEncoding.GainMap;
    private bool formatChosen;
    private double sourceWidth = 1;
    private double sourceHeight = 1;
    private int rotation;
    private uint backgroundColor = 0xFFFFFF;
    private decimal tileWidth = 512;
    private decimal tileHeight = 512;
    private bool updating;
    private bool sizing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsProgress))]
    private bool isOpen;

    [ObservableProperty]
    private IReadOnlyList<PhotoSaveChoice> formatChoices = [];

    [ObservableProperty]
    private PhotoSaveChoice? selectedFormat;

    [ObservableProperty]
    private IReadOnlyList<PhotoSaveChoice> encoderChoices = [];

    [ObservableProperty]
    private PhotoSaveChoice? selectedEncoder;

    [ObservableProperty]
    private IReadOnlyList<PhotoSaveChoice> pixelFormatChoices = [];

    [ObservableProperty]
    private PhotoSaveChoice? selectedPixelFormat;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QualitySlider))]
    private decimal quality = 90;

    [ObservableProperty]
    private PhotoSaveChoice? selectedSpeed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTileSize))]
    private bool saveTiled;

    [ObservableProperty]
    private decimal width = 1;

    [ObservableProperty]
    private decimal height = 1;

    [ObservableProperty]
    private decimal scale = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AspectLockToolTip))]
    private bool isAspectLocked = true;

    [ObservableProperty]
    private PhotoSaveChoice? selectedResize;

    [ObservableProperty]
    private PhotoSaveChoice? selectedColorSpace;

    [ObservableProperty]
    private PhotoSaveChoice? selectedGamutMapping;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MetadataDescription))]
    private PhotoSaveChoice? selectedMetadata;

    [ObservableProperty]
    private bool transparentBackground = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackgroundBrush))]
    private string backgroundText = "#FFFFFF";

    [ObservableProperty]
    private bool keepHdr = true;

    [ObservableProperty]
    private IReadOnlyList<PhotoSaveChoice> hdrEncodingChoices = [];

    [ObservableProperty]
    private PhotoSaveChoice? selectedHdrEncoding;

    [ObservableProperty]
    private decimal sdrWhiteNits = (decimal)PhotoExportColor.ReferenceWhiteNits;

    [ObservableProperty]
    private bool applyToneMapping;

    [ObservableProperty]
    private decimal targetPeakNits = 1000;

    [ObservableProperty]
    private IReadOnlyList<VideoSettingsOption> profileChoices = [];

    [ObservableProperty]
    private VideoSettingsOption? selectedProfile;

    /// <summary>The tone mapping of absolute-luminance pictures, or null for the built-in curve.</summary>
    [ObservableProperty]
    private VideoSdrToneMappingOptions? toneMapping;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyPropertyChangedFor(nameof(ShowsProgress))]
    private bool isSaving;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? errorMessage;

    internal PhotoSaveAsViewModel(IClientMediaResolver mediaResolver, IAppSettingsStore settingsStore, CancellationToken lifetime)
    {
        this.mediaResolver = mediaResolver;
        this.settingsStore = settingsStore;
        this.lifetime = lifetime;
        Editor.Edited += edited => ToneMapping = edited;
        SpeedChoices =
        [
            Choice(StillEncodeSpeed.Slower, nameof(Strings.Resources.Photo_SaveSpeedSlower)),
            Choice(StillEncodeSpeed.Balanced, nameof(Strings.Resources.Photo_SaveSpeedBalanced)),
            Choice(StillEncodeSpeed.Faster, nameof(Strings.Resources.Photo_SaveSpeedFaster)),
        ];
        ResizeChoices =
        [
            Choice(PhotoExportResize.Stretch, nameof(Strings.Resources.Photo_SaveResizeStretch)),
            Choice(PhotoExportResize.Fit, nameof(Strings.Resources.Photo_SaveResizeFit)),
            Choice(PhotoExportResize.Fill, nameof(Strings.Resources.Photo_SaveResizeFill)),
        ];
        ColorSpaceChoices =
        [
            Choice(PhotoExportColorSpace.Automatic, nameof(Strings.Resources.Photo_SaveColorSpaceAutomatic)),
            new PhotoSaveChoice(PhotoExportColorSpace.Srgb, () => "sRGB"),
            new PhotoSaveChoice(PhotoExportColorSpace.DisplayP3, () => "Display P3"),
            new PhotoSaveChoice(PhotoExportColorSpace.Rec2020, () => "Rec. 2020"),
        ];
        GamutMappingChoices =
        [
            Choice(PhotoExportGamutMapping.Compress, nameof(Strings.Resources.Photo_SaveGamutCompress)),
            Choice(PhotoExportGamutMapping.PreserveHue, nameof(Strings.Resources.Photo_SaveGamutPreserveHue)),
            Choice(PhotoExportGamutMapping.Clip, nameof(Strings.Resources.Photo_SaveGamutClip)),
        ];
        MetadataChoices =
        [
            Choice(PhotoExportMetadataMode.All, nameof(Strings.Resources.Photo_SaveMetadataAll)),
            Choice(PhotoExportMetadataMode.Rendering, nameof(Strings.Resources.Photo_SaveMetadataRendering)),
        ];
        selectedSpeed = SpeedChoices[1];
        selectedResize = ResizeChoices[0];
        selectedColorSpace = ColorSpaceChoices[0];
        selectedGamutMapping = GamutMappingChoices[1];
        selectedMetadata = MetadataChoices[0];
        CancelCommand = new RelayCommand(Cancel);
        CloseCommand = new RelayCommand(Close);
        ToggleAspectLockCommand = new RelayCommand(() => IsAspectLocked = !IsAspectLocked);
    }

    public ToneMappingEditorViewModel Editor { get; } = new(fixedTarget: true);

    public IReadOnlyList<PhotoSaveChoice> SpeedChoices { get; }

    public IReadOnlyList<PhotoSaveChoice> ResizeChoices { get; }

    public IReadOnlyList<PhotoSaveChoice> ColorSpaceChoices { get; }

    public IReadOnlyList<PhotoSaveChoice> GamutMappingChoices { get; }

    public IReadOnlyList<PhotoSaveChoice> MetadataChoices { get; }

    /// <summary>Cancels a save in progress, or closes the panel when nothing is being saved.</summary>
    public IRelayCommand CancelCommand { get; }

    public IRelayCommand CloseCommand { get; }

    public IRelayCommand ToggleAspectLockCommand { get; }

    public PhotoExportFormat Format => SelectedFormat?.Value is PhotoExportFormat format ? format : PhotoExportFormat.Jpeg;

    public StillImageEncoderInfo? Encoder => SelectedEncoder?.Value as StillImageEncoderInfo;

    public PhotoExportPixelFormat? PixelFormat => SelectedPixelFormat?.Value as PhotoExportPixelFormat?;

    public PhotoExportSourceKind Kind => source?.Kind ?? PhotoExportSourceKind.Sdr;

    /// <summary>How the saved file keeps HDR content.</summary>
    public PhotoExportHdrEncoding Hdr => Kind == PhotoExportSourceKind.Sdr || !KeepHdr ||
        SelectedHdrEncoding?.Value is not PhotoExportHdrEncoding encoding ? PhotoExportHdrEncoding.None : encoding;

    public bool ShowEncoder => EncoderChoices.Count > 1;

    public bool ShowPixelFormat => PixelFormatChoices.Count > 1;

    public bool ShowQuality => PixelFormat is { } pixelFormat && PhotoExportFormats.HasQuality(Format, pixelFormat);

    public bool ShowSpeed => PhotoExportFormats.HasSpeed(Format);

    public bool ShowTiles => Format is PhotoExportFormat.Avif or PhotoExportFormat.Heic && Encoder is not null;

    public bool ShowTileSize => ShowTiles && SaveTiled;

    /// <summary>A multiple of 64 within what the encoder codes and a HEIF grid holds.</summary>
    public decimal TileWidth
    {
        get => tileWidth;
        set => SetTileSize(ref tileWidth, value, MinimumTileWidth);
    }

    /// <inheritdoc cref="TileWidth"/>
    public decimal TileHeight
    {
        get => tileHeight;
        set => SetTileSize(ref tileHeight, value, MinimumTileHeight);
    }

    public decimal MinimumTileWidth => MinimumTile(Width);

    public decimal MinimumTileHeight => MinimumTile(Height);

    public decimal MaximumTileSize => Encoder?.MaximumDimension ?? MaximumSize / TileStep * TileStep;

    public double QualitySlider
    {
        get => (double)Quality;
        set => Quality = Math.Clamp((decimal)Math.Round(value), 1, 100);
    }

    /// <summary>The largest width or height the format stores.</summary>
    public decimal MaximumDimension => Format switch
    {
        PhotoExportFormat.WebP => 16383,
        PhotoExportFormat.Jpeg => 65500,
        _ => MaximumSize,
    };

    public decimal MaximumScale => Math.Max(0.01m, Math.Floor(MaximumDimension / (decimal)Math.Max(sourceWidth, sourceHeight) * 100) / 100);

    public string AspectLockToolTip => Localized(nameof(Strings.Resources.Photo_SaveKeepAspect));

    public bool ShowResize => !IsAspectLocked;

    private PhotoExportResize Resize => IsAspectLocked || SelectedResize?.Value is not PhotoExportResize resize
        ? PhotoExportResize.Stretch : resize;

    public bool CanBeTransparent => PixelFormat is { } pixelFormat && PhotoExportFormats.SupportsAlpha(Format, pixelFormat) &&
        Hdr != PhotoExportHdrEncoding.GainMap;

    /// <summary>Whether anything shows through: borders around the picture, or its own transparency.</summary>
    public bool ShowBackground => Resize == PhotoExportResize.Fit || source?.HasAlpha == true;

    public bool ShowTransparent => ShowBackground && CanBeTransparent;

    public bool ShowBackgroundColor => ShowBackground && !(CanBeTransparent && TransparentBackground);

    public IBrush BackgroundBrush => new SolidColorBrush(Color.FromUInt32(0xFF000000 | backgroundColor));

    public bool ShowHdr => Kind != PhotoExportSourceKind.Sdr;

    public bool ShowKeepHdr => ShowHdr && HdrEncodingChoices.Count > 0;

    /// <summary>Names the gain map where it is the only way the format keeps HDR; absolute sources have one created.</summary>
    public string KeepHdrLabel => Localized(HdrEncodingChoices is not [{ Value: PhotoExportHdrEncoding.GainMap }]
        ? nameof(Strings.Resources.Photo_SaveKeepHdr)
        : Kind == PhotoExportSourceKind.GainMap
            ? nameof(Strings.Resources.Photo_SaveKeepGainMap)
            : nameof(Strings.Resources.Photo_SaveCreateGainMap));

    public bool ShowHdrEncoding => ShowKeepHdr && KeepHdr && HdrEncodingChoices.Count > 1;

    public bool ShowSdrWhite => Kind == PhotoExportSourceKind.GainMap && Hdr == PhotoExportHdrEncoding.Pq;

    public bool ShowApplyToneMapping => Kind == PhotoExportSourceKind.Absolute && Hdr == PhotoExportHdrEncoding.Pq;

    public bool ShowTargetPeak => ShowApplyToneMapping && ApplyToneMapping;

    public bool ShowToneMapping => Kind == PhotoExportSourceKind.Absolute && (Hdr != PhotoExportHdrEncoding.Pq || ApplyToneMapping);

    public string? Warning => !ShowHdr ? null
        : HdrEncodingChoices.Count == 0 ? Localized(nameof(Strings.Resources.Photo_SaveWarnNoHdr))
        : !KeepHdr ? Localized(nameof(Strings.Resources.Photo_SaveWarnHdrLost))
        : Kind == PhotoExportSourceKind.GainMap && Hdr == PhotoExportHdrEncoding.Pq
            ? Localized(nameof(Strings.Resources.Photo_SaveWarnSdrLost))
        : Kind == PhotoExportSourceKind.Absolute && Hdr == PhotoExportHdrEncoding.GainMap
            ? Localized(nameof(Strings.Resources.Photo_SaveWarnLuminanceLost))
        : null;

    public bool HasWarning => Warning is not null;

    private PhotoExportColorSpace ColorSpace =>
        SelectedColorSpace?.Value is PhotoExportColorSpace colorSpace ? colorSpace : PhotoExportColorSpace.Automatic;

    private bool IsGray => PixelFormat is { } pixelFormat && PhotoExportFormats.IsGray(pixelFormat);

    /// <summary>Whether the picture has HDR or colors outside sRGB, which give the color space a choice.</summary>
    private bool IsWideSource => Kind != PhotoExportSourceKind.Sdr ||
        PhotoExportColor.ExceedsGamut(sourceGamuts, PhotoExportPrimaries.Bt709);

    public bool ShowColorSpace => !IsGray && Hdr != PhotoExportHdrEncoding.Pq && IsWideSource;

    /// <summary>
    /// Whether the picture's color spaces reach beyond the primaries it is saved in; the automatic choice
    /// is judged by the widest it picks.
    /// </summary>
    public bool HasGamutWarning => !IsGray && source is not null && PhotoExportColor.ExceedsGamut(sourceGamuts,
        Hdr == PhotoExportHdrEncoding.Pq ? PhotoExportPrimaries.Bt2020 : ColorSpace switch
        {
            PhotoExportColorSpace.Srgb => PhotoExportPrimaries.Bt709,
            PhotoExportColorSpace.Rec2020 => PhotoExportPrimaries.Bt2020,
            _ => PhotoExportPrimaries.DisplayP3,
        });

    public bool ShowColor => ShowColorSpace || HasGamutWarning;

    public string MetadataDescription => Localized(SelectedMetadata?.Value is PhotoExportMetadataMode.Rendering
        ? nameof(Strings.Resources.Photo_SaveMetadataRenderingDescription)
        : nameof(Strings.Resources.Photo_SaveMetadataAllDescription));

    public bool HasError => ErrorMessage is not null;

    public bool ShowsProgress => IsSaving && IsOpen;

    public bool CanSave => !IsSaving && source is not null && PixelFormat is not null &&
        (PhotoExportFormats.Codec(Format) is null || Encoder is not null);

    /// <summary>Whether the viewer shows the picture without its gain map while the panel is open.</summary>
    public bool HidesGainMap => IsOpen && Kind == PhotoExportSourceKind.GainMap && Hdr == PhotoExportHdrEncoding.None;

    /// <summary>Whether the viewer shows <see cref="PreviewToneMapping"/> while the panel is open.</summary>
    public bool OverridesToneMapping => IsOpen && ShowToneMapping;

    /// <summary>The tone mapping here, aimed at the target the file is saved for.</summary>
    public VideoSdrToneMappingOptions PreviewToneMapping
    {
        get
        {
            VideoSdrToneMappingOptions options = ToneMapping ?? Editor.AutomaticOptions;
            bool bt2446A = options.Algorithm == VideoSdrToneMappingAlgorithm.Bt2446A;
            double white = options.ResolveReferenceWhiteNits(null);
            if (Hdr == PhotoExportHdrEncoding.Pq)
            {
                double peak = (double)TargetPeakNits;
                return bt2446A
                    ? options with { TargetMaximumNits = peak }
                    : options with { TargetHeadroom = Math.Clamp(peak / white, 1, 100) };
            }

            return bt2446A
                ? options with { TargetMaximumNits = options.TargetMaximumNits ?? white }
                : options with { TargetHeadroom = 1 };
        }
    }

    public string SuggestedFileName
    {
        get
        {
            string name = Path.GetFileNameWithoutExtension(source?.FileName ?? string.Empty);
            return (name.Length > 0 ? name : "image") + PhotoExportFormats.Extension(Format);
        }
    }

    /// <summary>The folder holding the original, where the file picker starts when it is on this device.</summary>
    public string? StartFolder => source is { Media: { SourceId.IsLocal: true, Path: var path } } &&
        PlatformServices.SupportsLocalFileAccess && Path.IsPathFullyQualified(path)
            ? Path.GetDirectoryName(path)
            : null;

    internal void Open(PhotoSaveSource value)
    {
        Close();
        source = value;
        sourceGamuts = value.SourceGamuts;
        ErrorMessage = null;
        rotation = value.Rotation;
        (sourceWidth, sourceHeight) = value.Rotation % 180 == 0
            ? (Math.Max(1, value.Width), Math.Max(1, value.Height))
            : (Math.Max(1, value.Height), Math.Max(1, value.Width));
        preferredFormat = PreferredFormat(value.FileName);
        preferredHdr = value.Kind == PhotoExportSourceKind.Absolute ? PhotoExportHdrEncoding.Pq : PhotoExportHdrEncoding.GainMap;
        formatChosen = false;

        updating = true;
        try
        {
            KeepHdr = true;
            ApplyToneMapping = false;
            IsAspectLocked = true;
            SelectedResize = ResizeChoices[0];
            ProfileChoices = ToneMappingProfileChoices.Create(settingsStore.ToneMapping, includeBuiltIn: true);
            SelectedProfile = ToneMappingProfileChoices.Find(ProfileChoices, value.ProfileName);
            ToneMapping = value.ToneMapping;
            Editor.Options = value.ToneMapping;
        }
        finally
        {
            updating = false;
        }

        RebuildFormats(reset: true);
        ResetSize();
        IsOpen = true;
        encoderProbe ??= LoadEncodersAsync();
    }

    /// <summary>Follows a further turn of the picture while the panel is open.</summary>
    internal void Rotate(int value)
    {
        if (source is null || (value - rotation) % 180 == 0)
        {
            rotation = value;
            return;
        }

        rotation = value;
        (sourceWidth, sourceHeight) = (sourceHeight, sourceWidth);
        sizing = true;
        try
        {
            (Width, Height) = (Height, Width);
        }
        finally
        {
            sizing = false;
        }

        Refresh();
    }

    public void Close()
    {
        saving?.Cancel();
        IsOpen = false;
    }

    /// <summary>Encodes the picture and writes it to the stream <paramref name="openDestination"/> opens.</summary>
    public async Task SaveAsync(Func<Task<Stream>> openDestination)
    {
        if (!CanSave || source is not { } current)
            return;

        PhotoExportOptions options = BuildOptions();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        saving = cancellation;
        IsSaving = true;
        ErrorMessage = null;
        try
        {
            await using Stream data = await PlatformServices.PhotoExport.ExportAsync(mediaResolver, current.Media,
                current.RenderRaw, options, cancellation.Token).ConfigureAwait(true);
            cancellation.Token.ThrowIfCancellationRequested();
            await using (Stream destination = await openDestination().ConfigureAwait(true))
                await data.CopyToAsync(destination).ConfigureAwait(true);
            if (ReferenceEquals(source, current))
                IsOpen = false;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (PhotoExportException exception) when (exception.Failure == PhotoExportFailure.TooLarge)
        {
            AppLog.Warning("Photo.Export", $"{current.FileName} is too large to save at {options.Width}x{options.Height}.", exception);
            ErrorMessage = Localized(nameof(Strings.Resources.Photo_SaveTooLarge));
        }
        catch (Exception exception)
        {
            ErrorMessage = AppErrorReporter.LogAndGetMessage("Photo.Export", $"Could not save {current.FileName}.", exception);
        }
        finally
        {
            saving = null;
            IsSaving = false;
        }
    }

    private void Cancel()
    {
        if (saving is { } current)
            current.Cancel();
        else
            Close();
    }

    private PhotoExportOptions BuildOptions() => new()
    {
        Format = Format,
        Encoder = Encoder,
        PixelFormat = PixelFormat ?? PhotoExportPixelFormat.Yuv420,
        Quality = (int)Quality,
        Speed = SelectedSpeed?.Value is StillEncodeSpeed speed ? speed : StillEncodeSpeed.Balanced,
        TileSize = ShowTileSize ? ((int)TileWidth, (int)TileHeight) : null,
        Width = (int)Width,
        Height = (int)Height,
        Resize = Resize,
        TransparentBackground = CanBeTransparent && TransparentBackground,
        BackgroundColor = backgroundColor,
        Rotation = rotation,
        Hdr = Hdr,
        ToneMapping = ToneMapping,
        ToneMapHdr = ApplyToneMapping,
        TargetPeakNits = (double)TargetPeakNits,
        SdrWhiteNits = (double)SdrWhiteNits,
        ColorSpace = ColorSpace,
        GamutMapping = SelectedGamutMapping?.Value is PhotoExportGamutMapping mapping ? mapping : PhotoExportGamutMapping.PreserveHue,
        Metadata = SelectedMetadata?.Value is PhotoExportMetadataMode metadata ? metadata : PhotoExportMetadataMode.All,
    };

    private async Task LoadEncodersAsync()
    {
        encoders = await PlatformServices.PhotoExport.GetEncodersAsync().ConfigureAwait(true);
        RebuildFormats(reset: false);
    }

    private void RebuildFormats(bool reset)
    {
        PhotoExportFormat? current = SelectedFormat?.Value as PhotoExportFormat?;
        IReadOnlyList<PhotoSaveChoice> choices = Enum.GetValues<PhotoExportFormat>()
            .Where(format => PhotoExportFormats.Codec(format) is not { } codec || encoders.Any(encoder => encoder.Codec == codec))
            .Select(format => new PhotoSaveChoice(format, () => PhotoExportFormats.DisplayName(format)))
            .ToArray();
        PhotoExportFormat wanted = formatChosen && current is { } chosen ? chosen : preferredFormat;
        updating = true;
        try
        {
            FormatChoices = choices;
            SelectedFormat = choices.FirstOrDefault(choice => Equals(choice.Value, wanted)) ??
                choices.FirstOrDefault(choice => Equals(choice.Value, current)) ?? choices[0];
        }
        finally
        {
            updating = false;
        }

        if (reset || !Equals(SelectedFormat?.Value, current))
            OnFormatChanged();
    }

    private void OnFormatChanged()
    {
        PhotoExportFormat format = Format;
        updating = true;
        try
        {
            Quality = PhotoExportFormats.DefaultQuality(format);
            StillImageCodec? codec = PhotoExportFormats.Codec(format);
            EncoderChoices = codec is { } value
                ? encoders.Where(encoder => encoder.Codec == value).Select(encoder => new PhotoSaveChoice(encoder, () =>
                    encoder.IsHardware
                        ? string.Format(CultureInfo.CurrentCulture, Localized(nameof(Strings.Resources.Photo_SaveHardwareEncoder)),
                            encoder.DisplayName)
                        : encoder.DisplayName)).ToArray()
                : [];
            SelectedEncoder = EncoderChoices.FirstOrDefault();
        }
        finally
        {
            updating = false;
        }

        OnEncoderChanged(keepPixelFormat: false);
        if (Width > MaximumDimension || Height > MaximumDimension)
            ResetSize();
    }

    private void OnEncoderChanged(bool keepPixelFormat)
    {
        PhotoExportFormat format = Format;
        StillImageEncoderInfo? encoder = Encoder;
        var choices = new List<PhotoSaveChoice>();
        if (PhotoExportFormats.SupportsGainMap(format) &&
            PhotoExportFormats.PixelFormats(format, encoder, PhotoExportHdrEncoding.GainMap).Count > 0)
            choices.Add(Choice(PhotoExportHdrEncoding.GainMap, nameof(Strings.Resources.Photo_SaveHdrGainMap)));
        if (PhotoExportFormats.SupportsPq(format) &&
            PhotoExportFormats.PixelFormats(format, encoder, PhotoExportHdrEncoding.Pq).Count > 0)
            choices.Add(Choice(PhotoExportHdrEncoding.Pq, nameof(Strings.Resources.Photo_SaveHdrPq)));
        updating = true;
        try
        {
            HdrEncodingChoices = choices;
            SelectedHdrEncoding = choices.FirstOrDefault(choice => Equals(choice.Value, preferredHdr)) ?? choices.FirstOrDefault();
        }
        finally
        {
            updating = false;
        }

        OnHdrChanged(keepPixelFormat);
    }

    private void OnHdrChanged(bool keepPixelFormat)
    {
        PhotoExportFormat format = Format;
        PhotoExportHdrEncoding hdr = Hdr;
        PhotoExportPixelFormat? current = PixelFormat;
        bool alpha = source?.HasAlpha == true;
        PhotoExportPixelFormat preferred = format switch
        {
            PhotoExportFormat.Png or PhotoExportFormat.JpegXl => hdr == PhotoExportHdrEncoding.Pq
                ? alpha ? PhotoExportPixelFormat.Rgba16 : PhotoExportPixelFormat.Rgb16
                : alpha ? PhotoExportPixelFormat.Rgba8 : PhotoExportPixelFormat.Rgb8,
            PhotoExportFormat.Avif or PhotoExportFormat.Heic when hdr == PhotoExportHdrEncoding.Pq => PhotoExportPixelFormat.Yuv420P10,
            _ => PhotoExportPixelFormat.Yuv420,
        };
        IReadOnlyList<PhotoExportPixelFormat> available = PhotoExportFormats.PixelFormats(format, Encoder, hdr);
        updating = true;
        try
        {
            PixelFormatChoices = available.Select(pixelFormat =>
                new PhotoSaveChoice(pixelFormat, () => PixelFormatTitle(format, pixelFormat))).ToArray();
            PhotoExportPixelFormat? selected = keepPixelFormat && current is { } kept && available.Contains(kept)
                ? kept
                : available.Contains(preferred) ? preferred : available.FirstOrDefault();
            SelectedPixelFormat = PixelFormatChoices.FirstOrDefault(choice => Equals(choice.Value, selected));
            Editor.HdrTarget = hdr == PhotoExportHdrEncoding.Pq;
            Editor.AutomaticOptions = hdr == PhotoExportHdrEncoding.Pq
                ? VideoSdrToneMappingOptions.Default with { Algorithm = VideoSdrToneMappingAlgorithm.Bt2390, Bt2390KneeOffset = 0.5f }
                : VideoSdrToneMappingOptions.Default with { Algorithm = VideoSdrToneMappingAlgorithm.Spline };
        }
        finally
        {
            updating = false;
        }

        Refresh();
    }

    /// <summary>Raises change notifications for everything worked out from the choices above.</summary>
    private void Refresh()
    {
        foreach (string name in DerivedProperties)
            OnPropertyChanged(name);
        RefreshTiles();
    }

    private static readonly string[] DerivedProperties =
    [
        nameof(Format), nameof(Encoder), nameof(PixelFormat), nameof(Kind), nameof(Hdr), nameof(ShowEncoder),
        nameof(ShowPixelFormat), nameof(ShowQuality), nameof(ShowSpeed), nameof(ShowTiles), nameof(ShowTileSize),
        nameof(MaximumTileSize), nameof(MaximumDimension), nameof(MaximumScale),
        nameof(ShowResize), nameof(CanBeTransparent), nameof(ShowBackground), nameof(ShowTransparent),
        nameof(ShowBackgroundColor), nameof(ShowHdr), nameof(ShowKeepHdr), nameof(KeepHdrLabel), nameof(ShowHdrEncoding),
        nameof(ShowSdrWhite), nameof(ShowApplyToneMapping), nameof(ShowTargetPeak), nameof(ShowToneMapping), nameof(Warning),
        nameof(HasWarning), nameof(ShowColorSpace), nameof(HasGamutWarning), nameof(ShowColor), nameof(CanSave),
        nameof(HidesGainMap), nameof(OverridesToneMapping), nameof(PreviewToneMapping),
        nameof(SuggestedFileName), nameof(StartFolder),
    ];

    /// <summary>Pulls the tile size back within limits that follow the encoder and the picture size.</summary>
    private void RefreshTiles()
    {
        OnPropertyChanged(nameof(MinimumTileWidth));
        OnPropertyChanged(nameof(MinimumTileHeight));
        TileWidth = tileWidth;
        TileHeight = tileHeight;
    }

    private void SetTileSize(ref decimal field, decimal value, decimal minimum, [CallerMemberName] string? propertyName = null)
    {
        decimal snapped = Math.Clamp(Math.Round(value / TileStep) * TileStep, minimum, Math.Max(minimum, MaximumTileSize));
        SetProperty(ref field, snapped, propertyName);
    }

    /// <summary>The smallest tile the encoder codes exactly that still keeps a grid within 256 tiles across.</summary>
    private decimal MinimumTile(decimal dimension) => Math.Max(
        Encoder is { } encoder ? HeifImageEncoder.MinimumTileSize(encoder) : TileStep,
        Math.Ceiling(Math.Ceiling(dimension / MaximumTilesPerAxis) / TileStep) * TileStep);

    partial void OnIsOpenChanged(bool value)
    {
        if (!value)
            ErrorMessage = null;
        Refresh();
    }

    partial void OnSelectedFormatChanged(PhotoSaveChoice? value)
    {
        if (updating || value is null)
            return;
        formatChosen = true;
        OnFormatChanged();
    }

    partial void OnSelectedEncoderChanged(PhotoSaveChoice? value)
    {
        if (!updating && value is not null)
            OnEncoderChanged(keepPixelFormat: true);
    }

    partial void OnSelectedPixelFormatChanged(PhotoSaveChoice? value)
    {
        if (!updating)
            Refresh();
    }

    partial void OnSelectedHdrEncodingChanged(PhotoSaveChoice? value)
    {
        if (updating || value?.Value is not PhotoExportHdrEncoding encoding)
            return;
        preferredHdr = encoding;
        OnHdrChanged(keepPixelFormat: true);
    }

    partial void OnKeepHdrChanged(bool value)
    {
        if (!updating)
            OnHdrChanged(keepPixelFormat: true);
    }

    partial void OnApplyToneMappingChanged(bool value) => Refresh();

    partial void OnTargetPeakNitsChanged(decimal value) => Refresh();

    partial void OnSelectedResizeChanged(PhotoSaveChoice? value) => Refresh();

    partial void OnSelectedColorSpaceChanged(PhotoSaveChoice? value) => Refresh();

    partial void OnTransparentBackgroundChanged(bool value) => Refresh();

    partial void OnBackgroundTextChanged(string value)
    {
        if (ParseColor(value) is { } color)
        {
            backgroundColor = color;
            OnPropertyChanged(nameof(BackgroundBrush));
        }
    }

    partial void OnSelectedProfileChanged(VideoSettingsOption? value)
    {
        if (updating)
            return;
        // A copy of the profile: adjustments here are not saved back to it.
        ToneMapping = settingsStore.ToneMapping.Find(ToneMappingProfileChoices.ProfileName(value))?.Options;
    }

    partial void OnToneMappingChanged(VideoSdrToneMappingOptions? value)
    {
        Editor.Options = value;
        Refresh();
    }

    partial void OnWidthChanged(decimal value)
    {
        RefreshTiles();
        if (sizing)
            return;
        sizing = true;
        try
        {
            if (IsAspectLocked)
            {
                Height = Dimension((double)value * sourceHeight / sourceWidth);
                Scale = ScaleOf(value);
            }
        }
        finally
        {
            sizing = false;
        }
    }

    partial void OnHeightChanged(decimal value)
    {
        RefreshTiles();
        if (sizing)
            return;
        sizing = true;
        try
        {
            if (IsAspectLocked)
            {
                Width = Dimension((double)value * sourceWidth / sourceHeight);
                Scale = ScaleOf(Width);
            }
        }
        finally
        {
            sizing = false;
        }
    }

    partial void OnScaleChanged(decimal value)
    {
        if (sizing)
            return;
        sizing = true;
        try
        {
            Width = Dimension(sourceWidth * (double)value);
            Height = Dimension(sourceHeight * (double)value);
        }
        finally
        {
            sizing = false;
        }
    }

    partial void OnIsAspectLockedChanged(bool value)
    {
        if (value)
        {
            sizing = true;
            try
            {
                Height = Dimension((double)Width * sourceHeight / sourceWidth);
                Scale = ScaleOf(Width);
            }
            finally
            {
                sizing = false;
            }
        }

        Refresh();
    }

    /// <summary>The full size, or the largest the format stores with the same shape.</summary>
    private void ResetSize()
    {
        double fit = Math.Min(1, (double)MaximumDimension / Math.Max(sourceWidth, sourceHeight));
        sizing = true;
        try
        {
            Width = Dimension(sourceWidth * fit);
            Height = Dimension(sourceHeight * fit);
            Scale = ScaleOf(Width);
        }
        finally
        {
            sizing = false;
        }

        Refresh();
    }

    private decimal Dimension(double value) =>
        Math.Clamp((decimal)Math.Round(double.IsFinite(value) ? value : 1), 1, MaximumDimension);

    private decimal ScaleOf(decimal width) =>
        Math.Clamp(Math.Round(width / (decimal)sourceWidth, 2), 0.01m, MaximumScale);

    private static uint? ParseColor(string text)
    {
        string hex = text.Trim().TrimStart('#');
        if (hex.Length == 3)
            hex = string.Concat(hex.Select(digit => new string(digit, 2)));
        return hex.Length == 6 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value)
            ? value
            : null;
    }

    private static PhotoExportFormat PreferredFormat(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".png" => PhotoExportFormat.Png,
        ".webp" => PhotoExportFormat.WebP,
        ".avif" => PhotoExportFormat.Avif,
        ".heic" or ".heif" or ".hif" => PhotoExportFormat.Heic,
        ".jxl" => PhotoExportFormat.JpegXl,
        _ => PhotoExportFormat.Jpeg,
    };

    private static string PixelFormatTitle(PhotoExportFormat format, PhotoExportPixelFormat pixelFormat)
    {
        int depth = PhotoExportFormats.BitDepth(pixelFormat);
        string title = pixelFormat switch
        {
            PhotoExportPixelFormat.Rgb8 or PhotoExportPixelFormat.Rgb16 =>
                FormatText(nameof(Strings.Resources.Photo_SavePixelRgb), depth),
            PhotoExportPixelFormat.Rgba8 or PhotoExportPixelFormat.Rgba16 =>
                FormatText(nameof(Strings.Resources.Photo_SavePixelRgba), depth),
            _ when PhotoExportFormats.IsGray(pixelFormat) => FormatText(nameof(Strings.Resources.Photo_SavePixelGray), depth),
            PhotoExportPixelFormat.Yuv420 or PhotoExportPixelFormat.Yuv420P10 or PhotoExportPixelFormat.Yuv420P12 =>
                FormatText(nameof(Strings.Resources.Photo_SavePixelYuv), "4:2:0", depth),
            PhotoExportPixelFormat.Yuv422 or PhotoExportPixelFormat.Yuv422P10 or PhotoExportPixelFormat.Yuv422P12 =>
                FormatText(nameof(Strings.Resources.Photo_SavePixelYuv), "4:2:2", depth),
            _ => FormatText(nameof(Strings.Resources.Photo_SavePixelYuv), "4:4:4", depth),
        };
        return format == PhotoExportFormat.WebP && PhotoExportFormats.IsLosslessWebP(pixelFormat)
            ? FormatText(nameof(Strings.Resources.Photo_SavePixelLossless), title)
            : title;
    }

    private static string FormatText(string key, params object[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Localized(key), arguments);

    private static PhotoSaveChoice Choice(object value, string titleKey) => new(value, () => Localized(titleKey));
}
