using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.ViewModels.Pages;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Views.Pages;

public partial class RemoteApiSettingsSectionView : UserControl
{
    private readonly DispatcherTimer pairingQrExpiryTimer;
    private Bitmap? pairingQrBitmap;

    public RemoteApiSettingsSectionView()
    {
        InitializeComponent();
        pairingQrExpiryTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        pairingQrExpiryTimer.Tick += (_, _) => ClearPairingQrCode();
    }

    private async void CopyPairingLink(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not RemoteApiSettingsSectionViewModel viewModel ||
            viewModel.CreatePairingLink() is not { } link)
        {
            return;
        }

        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            {
                viewModel.NotifyPairingLinkCopyFailed();
                return;
            }

            await clipboard.SetTextAsync(link);
            viewModel.NotifyPairingLinkCopied();
        }
        catch (Exception exception)
        {
            AppLog.Warning("RemoteApi.Pairing", "The pairing link could not be copied.", exception);
            viewModel.NotifyPairingLinkCopyFailed();
        }
    }

    private void ShowPairingQrCode(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not RemoteApiSettingsSectionViewModel viewModel ||
            viewModel.CreateAndroidPairingUri() is not { } uri)
        {
            return;
        }

        try
        {
            var bitmap = PairingQrCode.Create(uri.AbsoluteUri);
            PairingQrImage.Source = null;
            pairingQrBitmap?.Dispose();
            pairingQrBitmap = bitmap;
            PairingQrImage.Source = bitmap;
            PairingQrPanel.IsVisible = true;
            pairingQrExpiryTimer.Stop();
            pairingQrExpiryTimer.Start();
        }
        catch (Exception exception)
        {
            AppLog.Warning("RemoteApi.Pairing", "The pairing QR code could not be created.", exception);
            viewModel.NotifyPairingQrCodeFailed();
        }
    }

    private void HidePairingQrCode(object? sender, RoutedEventArgs args) => ClearPairingQrCode();

    private void ClearPairingQrCode()
    {
        pairingQrExpiryTimer.Stop();
        PairingQrPanel.IsVisible = false;
        PairingQrImage.Source = null;
        pairingQrBitmap?.Dispose();
        pairingQrBitmap = null;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ClearPairingQrCode();
        base.OnDetachedFromVisualTree(e);
    }
}