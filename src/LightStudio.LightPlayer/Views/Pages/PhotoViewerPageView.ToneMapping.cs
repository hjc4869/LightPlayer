using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using LightStudio.LightPlayer.ViewModels.Pages;

namespace LightStudio.LightPlayer.Views.Pages;

public partial class PhotoViewerPageView
{
    private Flyout HdrFlyout => (Flyout)HdrButton.Flyout!;

    private void OnPhotoModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(PhotoViewerPageViewModel.NavigationParameter))
            HdrFlyout.Hide();
        if (args.PropertyName == nameof(PhotoViewerPageViewModel.HasAbsoluteLuminance))
            UpdateHdrControls();
    }

    private void UpdateHdrControls()
    {
        if (HdrButton is null)
            return;
        HdrButton.IsVisible = ViewModel?.HasAbsoluteLuminance == true;
        if (!HdrButton.IsVisible)
            HdrFlyout.Hide();
        if (ViewModel is { } model)
        {
            model.ToneProfiles.Editor.AutomaticOptions = Photo.AutomaticToneMapping;
            model.ToneProfiles.Editor.DisplayTarget = Photo.DisplayToneTarget;
        }
    }

    private void OnHdrFlyoutOpening(object? sender, EventArgs args)
    {
        UpdateToneMappingPopupSize();
        UpdateHdrControls();
    }

    private void UpdateToneMappingPopupSize()
    {
        if (ToneMappingContent is null)
            return;
        double availableWidth = Bounds.Width - 64;
        if (HdrButton.TranslatePoint(default, this) is { } position)
        {
            double center = position.X + HdrButton.Bounds.Width / 2;
            availableWidth = Math.Min(availableWidth, 2 * Math.Min(center, Bounds.Width - center) - 48);
        }
        ToneMappingContent.Width = Math.Max(160, Math.Min(340, availableWidth));
        ToneMappingScroll.MaxHeight = Math.Max(80, Bounds.Height - 100);
    }
}
