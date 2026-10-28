using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim;
using LightStudio.FfmpegShim.Engine;
using LightStudio.FfmpegShim.ImageEncoding;
using LightStudio.LightPlayer.Services.PhotoExport;

namespace LightStudio.LightPlayer.WebWorkers;

internal static partial class WorkerPhotos
{
    /// <summary>
    /// Saves a photo with the apps' save pipeline. The file follows the result in native memory; a failure the save
    /// panel explains comes back as the result instead.
    /// </summary>
    public static async Task<string> ExportAsync(WirePhotoExport request, CancellationToken cancellationToken)
    {
        StillImageEncoderInfo? encoder = request.Encoder is not { } name ? null
            : (await StillImageEncoders.ProbeAsync()).FirstOrDefault(candidate => candidate.Name == name)
                ?? throw new NotSupportedException($"The media worker has no {name} encoder.");
        using var source = new SourceBridgeContext(request.Source.Id, (long)request.Source.Length, WindowBytes, Windows, Prefetch);
        MemoryStream encoded;
        try
        {
            encoded = await PhotoExporter.ExportAsync(
                token => Task.FromResult(PhotoExporter.ReadMetadata(source, request.Source.Name, token)),
                _ =>
                {
                    source.Position = 0;
                    return Task.FromResult<VideoFrame?>(DecodeWhole(source, request.Source.Name, request.RenderRaw));
                },
                request.Options with { Encoder = encoder }, cancellationToken);
        }
        catch (PhotoExportException exception)
        {
            return Payloads.Envelope(JsonSerializer.Serialize(exception.Failure.ToString(), EngineWireContext.Default.String));
        }

        using (encoded)
            return Payloads.Envelope("null", Payloads.Copy(encoded.GetBuffer().AsSpan(0, (int)encoded.Length)));
    }

    /// <summary>
    /// Decodes a whole photo, with tile grids assembled, and gives it the color and gain map the page shows it with.
    /// </summary>
    private static VideoFrame DecodeWhole(Stream stream, string name, bool? renderRaw)
    {
        var frames = new List<VideoFrame>();
        var session = new Session();
        VideoFrame? frame = null;
        try
        {
            WirePhoto photo = OpenPicture(stream, name, renderRaw, frames, session, useTiles: false);
            bool hasGainMap = WirePhotoFrames.HasGainMap(photo);
            PhotoColorProfile? profile = WirePhotoFrames.BaseProfile(photo);
            frame = frames[photo.Base.Frame].AddRef();
            if (profile is not null)
                WirePhotoFrames.ApplyProfile(frame, profile, linear: hasGainMap);
            if (hasGainMap)
            {
                VideoFrame samples = frames[photo.Gain!.Frame];
                WirePhotoFrames.ApplyProfile(samples, WirePhotoFrames.GainSampleProfile, linear: true);
                frame.GainMap = WirePhotoFrames.CreateGainMap(photo, profile, samples);
            }

            VideoFrame result = frame;
            frame = null;
            return result;
        }
        finally
        {
            frame?.Release();
            foreach (VideoFrame decoded in frames)
                decoded.Release();
            session.Dispose();
        }
    }
}
