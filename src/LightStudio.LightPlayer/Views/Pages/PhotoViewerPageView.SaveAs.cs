using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LightMediaRenderer.Avalonia.Controls;
using LightStudio.LightPlayer.Controls;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Services.PhotoExport;
using LightStudio.LightPlayer.ViewModels.Pages;

namespace LightStudio.LightPlayer.Views.Pages;

public partial class PhotoViewerPageView
{
    private const double SaveAsPanelWidth = 380;
    private const double SaveAsSlideMs = 220d;

    private readonly TranslateTransform saveAsOffset = new();
    private readonly Glide saveAsGlide;
    private double saveAsTravel = SaveAsPanelWidth;
    private bool saveAsClosing;

    private void OnSaveAsPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(PhotoSaveAsViewModel.IsOpen) && ViewModel is { } model)
            SetSaveAsOpen(model.SaveAs.IsOpen, animate: true);
    }

    /// <summary>Keeps the panel within narrow pages.</summary>
    private void UpdateSaveAsWidth() =>
        SaveAsPanel.Width = Math.Max(0, Math.Min(SaveAsPanelWidth, Bounds.Width - SaveAsPanel.Margin.Left - SaveAsPanel.Margin.Right));

    /// <summary>
    /// Shows the tile size the panel kept after snapping a typed one to a multiple of 64; the binding
    /// that wrote the typed value does not carry back a value it already saw.
    /// </summary>
    private void FollowSnappedTile(NumberEntry entry, Func<PhotoSaveAsViewModel, decimal> kept) =>
        entry.PropertyChanged += (_, args) =>
        {
            if (args.Property != NumberEntry.NumberProperty)
                return;
            Dispatcher.UIThread.Post(() =>
            {
                if (ViewModel?.SaveAs is { } saveAs && entry.Number != kept(saveAs))
                    entry.SetCurrentValue(NumberEntry.NumberProperty, kept(saveAs));
            });
        };

    /// <summary>Slides the save panel in from the right edge, and back out over it when it closes.</summary>
    private void SetSaveAsOpen(bool open, bool animate)
    {
        if (open && !SaveAsPanel.IsVisible)
            UpdateSaveAsWidth();
        saveAsTravel = SaveAsPanel.Width + SaveAsPanel.Margin.Right;
        if (open)
        {
            saveAsClosing = false;
            if (!SaveAsPanel.IsVisible)
            {
                saveAsOffset.X = animate ? saveAsTravel : 0d;
                SaveAsPanel.IsVisible = true;
            }

            if (animate)
            {
                saveAsGlide.Start(saveAsOffset.X, 0d, SaveAsSlideMs);
            }
            else
            {
                saveAsGlide.Stop();
                saveAsOffset.X = 0d;
            }

            return;
        }

        if (animate && SaveAsPanel.IsVisible)
        {
            saveAsClosing = true;
            saveAsGlide.Start(saveAsOffset.X, saveAsTravel, SaveAsSlideMs);
            return;
        }

        saveAsGlide.Stop();
        saveAsClosing = false;
        saveAsOffset.X = 0d;
        SaveAsPanel.IsVisible = false;
    }

    private void ApplySaveAsOffset(double offset)
    {
        saveAsOffset.X = offset;
        if (saveAsClosing && offset >= saveAsTravel - 0.5)
        {
            saveAsClosing = false;
            SaveAsPanel.IsVisible = false;
        }
    }

    private void OnSaveAsConfirmClick(object? sender, RoutedEventArgs e) => _ = AppErrorReporter.GuardAsync(SaveAsAsync);

    private async Task SaveAsAsync()
    {
        if (ViewModel?.SaveAs is not { CanSave: true } saveAs ||
            TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage)
            return;

        PhotoExportFormat format = saveAs.Format;
        string extension = PhotoExportFormats.Extension(format);
        IStorageFolder? start = saveAs.StartFolder is { } folder
            ? await storage.TryGetFolderFromPathAsync(folder).ConfigureAwait(true)
            : null;
        IStorageFile? file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Strings.Resources.Photo_SaveAs,
            SuggestedFileName = saveAs.SuggestedFileName,
            SuggestedStartLocation = start,
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

        using (file)
        {
            await saveAs.SaveAsync(async () =>
            {
                Stream stream = await file.OpenWriteAsync().ConfigureAwait(true);
                if (stream.CanSeek)
                    stream.SetLength(0);
                return stream;
            }).ConfigureAwait(true);
        }
    }
}
