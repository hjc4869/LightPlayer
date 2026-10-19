using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using LightStudio.NativeBindings.Types;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Demuxing;
using LightStudio.FfmpegShim.Media;
using LightStudio.FfmpegShim.Subtitles;
using LightMediaRenderer.Avalonia.Pipeline;

namespace LightStudio.VideoValidation;

public sealed class RawVideoSourceProvider : IMediaSourceProvider
{
    public const int FrameRate = 12;

    private readonly string path;
    private readonly ValidationAsset asset;

    public RawVideoSourceProvider(string path, ValidationAsset asset)
    {
        this.path = Path.GetFullPath(path);
        this.asset = asset;
    }

    public string Description => "validation raw frames";

    public IPacketSource OpenMain() => new RawVideoPacketSource(path, asset);

    public IPacketSource OpenSidecar(string path) =>
        throw new NotSupportedException("Raw validation assets have no sidecar streams.");

    public string ReadSubtitleScript(int streamIndex, string? sidecarPath, CancellationToken token = default) =>
        throw new NotSupportedException("Raw validation assets have no subtitle streams.");

    public BitmapSubtitleDocument ReadBitmapSubtitles(
        int streamIndex,
        string? sidecarPath,
        CancellationToken token = default) =>
        throw new NotSupportedException("Raw validation assets have no subtitle streams.");

    public Stream OpenFile(string path) => File.OpenRead(path);

    public void Dispose()
    {
    }

    public static int FrameByteCount(ValidationAsset asset)
    {
        VideoPlanarLayout layout = VideoPlanarLayout.For(asset.ExpectedFormat);
        if (asset.ExpectedFormat == VideoPixelFormat.Bgra32)
            return checked(asset.Width * asset.Height * 4);

        int luma = checked(asset.Width * asset.Height * layout.BytesPerSample);
        int chromaPlane = checked(
            layout.ChromaWidth(asset.Width) *
            layout.ChromaHeight(asset.Height) *
            layout.BytesPerSample);
        return checked(luma + chromaPlane * (layout.InterleavedChroma ? 2 : 2));
    }

    private sealed class RawVideoPacketSource : IPacketSource
    {
        private const int StreamIndex = 0;

        private readonly FileStream stream;
        private readonly byte[] frameBuffer;
        private readonly CodecParameters parameters;
        private readonly int frameCount;
        private int frameIndex;

        public RawVideoPacketSource(string path, ValidationAsset asset)
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            frameBuffer = new byte[FrameByteCount(asset)];
            if (stream.Length == 0 || stream.Length % frameBuffer.Length != 0)
            {
                stream.Dispose();
                throw new InvalidDataException(
                    $"Raw asset '{path}' is {stream.Length} bytes; expected whole {frameBuffer.Length}-byte frames.");
            }

            frameCount = checked((int)(stream.Length / frameBuffer.Length));
            parameters = CreateParameters(asset);

            var video = new MediaStreamInfo(
                StreamIndex,
                MediaStreamKind.Video,
                "rawvideo",
                string.Empty,
                asset.Id,
                IsDefault: true)
            {
                Duration = TimeSpan.FromSeconds(frameCount / (double)FrameRate),
                Width = asset.Width,
                Height = asset.Height,
                FrameRate = FrameRate,
                PixelFormat = asset.PixelFormat,
            };

            Info = new MediaInfo(
                path,
                "raw validation frames",
                video.Duration,
                checked(stream.Length * 8 * FrameRate / frameCount),
                CanSeek: true,
                [video],
                new Dictionary<string, string>())
            {
                DefaultVideoStream = video,
            };
        }

        public MediaInfo Info { get; }

        public int VideoStreamIndex => StreamIndex;

        public int AudioStreamIndex => -1;

        public bool CanSeek => true;

        public CodecParameters GetCodecParameters(int streamIndex) => streamIndex == StreamIndex
            ? parameters
            : throw new ArgumentOutOfRangeException(nameof(streamIndex));

        public void SelectAudioStream(int streamIndex)
        {
            if (streamIndex != -1)
                throw new ArgumentOutOfRangeException(nameof(streamIndex));
        }

        public PacketReadStatus Read(MediaPacket packet)
        {
            if (frameIndex >= frameCount)
                return PacketReadStatus.EndOfFile;

            stream.ReadExactly(frameBuffer);
            packet.FillFromWire(
                StreamIndex,
                frameIndex,
                frameIndex,
                duration: 1,
                isKeyFrame: true,
                frameBuffer);
            frameIndex++;
            return PacketReadStatus.Ok;
        }

        public bool Seek(double positionSeconds)
        {
            frameIndex = Math.Clamp((int)Math.Floor(positionSeconds * FrameRate), 0, frameCount);
            stream.Position = (long)frameIndex * frameBuffer.Length;
            return true;
        }

        public double ToSeconds(int streamIndex, long timestamp)
        {
            if (streamIndex != StreamIndex)
                return double.NaN;
            return timestamp / (double)FrameRate;
        }

        public void Dispose() => stream.Dispose();

        private static CodecParameters CreateParameters(ValidationAsset asset) => new()
        {
            Kind = MediaStreamKind.Video,
            CodecName = "rawvideo",
            PixelFormat = asset.PixelFormat,
            BitsPerRawSample = VideoPlanarLayout.For(asset.ExpectedFormat).BitDepth,
            Width = asset.Width,
            Height = asset.Height,
            SampleAspectRatioNum = 1,
            SampleAspectRatioDen = 1,
            FrameRateNum = FrameRate,
            FrameRateDen = 1,
            TimeBaseNum = 1,
            TimeBaseDen = FrameRate,
            ColorRange = (int)(asset.ColorRange == VideoColorRange.Full
                ? AVColorRange.AVCOL_RANGE_JPEG
                : AVColorRange.AVCOL_RANGE_MPEG),
            ColorPrimaries = (int)(asset.ColorPrimaries switch
            {
                VideoColorPrimaries.Bt2020 => AVColorPrimaries.AVCOL_PRI_BT2020,
                VideoColorPrimaries.Smpte170M => AVColorPrimaries.AVCOL_PRI_SMPTE170M,
                VideoColorPrimaries.Bt470Bg => AVColorPrimaries.AVCOL_PRI_BT470BG,
                VideoColorPrimaries.Bt470M => AVColorPrimaries.AVCOL_PRI_BT470M,
                VideoColorPrimaries.DisplayP3 => AVColorPrimaries.AVCOL_PRI_SMPTE432,
                VideoColorPrimaries.DciP3 => AVColorPrimaries.AVCOL_PRI_SMPTE431,
                _ => AVColorPrimaries.AVCOL_PRI_BT709,
            }),
            ColorTransfer = (int)(asset.ColorTransfer switch
            {
                VideoColorTransfer.Pq => AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084,
                VideoColorTransfer.Hlg => AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67,
                VideoColorTransfer.Srgb => AVColorTransferCharacteristic.AVCOL_TRC_IEC61966_2_1,
                VideoColorTransfer.Linear => AVColorTransferCharacteristic.AVCOL_TRC_LINEAR,
                _ => AVColorTransferCharacteristic.AVCOL_TRC_BT709,
            }),
            ColorSpace = (int)(asset.ColorSpace switch
            {
                VideoColorSpace.Bt601 => AVColorSpace.AVCOL_SPC_SMPTE170M,
                VideoColorSpace.Bt2020 => AVColorSpace.AVCOL_SPC_BT2020_NCL,
                _ => AVColorSpace.AVCOL_SPC_BT709,
            }),
            ChromaLocation = (int)(asset.ChromaLocation switch
            {
                VideoChromaLocation.Center => AVChromaLocation.AVCHROMA_LOC_CENTER,
                VideoChromaLocation.TopLeft => AVChromaLocation.AVCHROMA_LOC_TOPLEFT,
                VideoChromaLocation.Top => AVChromaLocation.AVCHROMA_LOC_TOP,
                VideoChromaLocation.BottomLeft => AVChromaLocation.AVCHROMA_LOC_BOTTOMLEFT,
                VideoChromaLocation.Bottom => AVChromaLocation.AVCHROMA_LOC_BOTTOM,
                _ => AVChromaLocation.AVCHROMA_LOC_LEFT,
            }),
        };
    }
}