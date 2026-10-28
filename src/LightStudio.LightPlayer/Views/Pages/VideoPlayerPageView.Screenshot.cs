using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using LightMediaRenderer.Contracts;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Services.PhotoExport;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Views.Pages;

public partial class VideoPlayerPageView
{
    /// <summary>Longest wait for a drawn frame when the decoded one cannot be read.</summary>
    private static readonly TimeSpan ScreenshotCaptureTimeout = TimeSpan.FromSeconds(5);

    private const int MaximumScreenshotNameLength = 120;

    private bool savingScreenshot;

    private void OnScreenshotClick(object? sender, RoutedEventArgs e) => SaveScreenshot();

    /// <summary>Captures the frame on screen, encodes it, and asks where to save it.</summary>
    internal void SaveScreenshot() => _ = SaveScreenshotAsync();

    private async Task SaveScreenshotAsync()
    {
        if (savingScreenshot || ViewModel is not { CanSaveScreenshot: true, IsPictureDetached: false } model ||
            PlatformServices.VideoScreenshotEncoder is not { } encoder)
            return;

        savingScreenshot = true;
        using var cancellation = new CancellationTokenSource();
        VideoFrameSnapshot? snapshot = null;
        Task<byte[]>? encoding = null;
        bool encodingAwaited = false;
        try
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage)
                throw new NotSupportedException("This platform cannot save files.");

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token))
            {
                timeout.CancelAfter(ScreenshotCaptureTimeout);
                snapshot = await Video.CaptureFrameAsync(timeout.Token).ConfigureAwait(true);
            }

            // Encoding runs while the picker is open, so the file is usually ready when a place is chosen.
            PhotoExportFormat format = await encoder.ChooseFormatAsync(snapshot).ConfigureAwait(true);
            encoding = encoder.EncodeAsync(snapshot, format, cancellation.Token);
            string extension = PhotoExportFormats.Extension(format);
            IStorageFile? file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Strings.Resources.VideoPlayer_Screenshot,
                SuggestedFileName = SuggestedScreenshotName(model.MediaTitle, snapshot.Timestamp) + extension,
                SuggestedStartLocation = await storage.TryGetWellKnownFolderAsync(WellKnownFolder.Pictures).ConfigureAwait(true),
                DefaultExtension = extension.TrimStart('.'),
                ShowOverwritePrompt = true,
                FileTypeChoices =
                [
                    new FilePickerFileType(PhotoExportFormats.DisplayName(format))
                    {
                        Patterns = ["*" + extension],
                        MimeTypes = [PhotoExportFormats.MimeType(format)],
                        AppleUniformTypeIdentifiers = [PhotoExportFormats.AppleTypeIdentifier(format)],
                    },
                ],
            }).ConfigureAwait(true);
            if (file is null)
                return;

            encodingAwaited = true;
            byte[] encoded = await encoding.ConfigureAwait(true);
            using (file)
            {
                await using Stream stream = await file.OpenWriteAsync().ConfigureAwait(true);
                if (stream.CanSeek)
                    stream.SetLength(0);
                await stream.WriteAsync(encoded).ConfigureAwait(true);
            }

            ShowToast(Strings.Resources.VideoPlayer_ScreenshotSaved);
        }
        catch (Exception exception)
        {
            AppLog.Error("Video.Screenshot", "Could not save a screenshot.", exception);
            model.ErrorMessage = Strings.Resources.VideoPlayer_ScreenshotFailed;
        }
        finally
        {
            // The snapshot backs the encoder's reads, so it outlives whatever encoding is still running.
            if (encoding is not null && !encodingAwaited)
            {
                cancellation.Cancel();
                await DiscardEncodingAsync(encoding).ConfigureAwait(true);
            }

            snapshot?.Dispose();
            savingScreenshot = false;
        }
    }

    private static async Task DiscardEncodingAsync(Task encoding)
    {
        try
        {
            await encoding.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            AppLog.Warning("Video.Screenshot", "A screenshot that was not saved failed to encode.", exception);
        }
    }

    /// <summary>The video's title followed by the frame's position, with characters no file system takes replaced.</summary>
    private static string SuggestedScreenshotName(string title, TimeSpan timestamp)
    {
        var name = new StringBuilder(Math.Min(title.Length, MaximumScreenshotNameLength));
        foreach (char character in title.Trim())
        {
            if (name.Length == MaximumScreenshotNameLength)
                break;
            name.Append(char.IsControl(character) || "\\/:*?\"<>|".Contains(character) ? '_' : character);
        }

        string position = string.Create(CultureInfo.InvariantCulture,
            $"{(int)timestamp.TotalHours:00}-{timestamp.Minutes:00}-{timestamp.Seconds:00}.{timestamp.Milliseconds:000}");
        return name.Length > 0 ? $"{name.ToString().TrimEnd('.', ' ')} {position}" : position;
    }
}
