using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using System;

namespace LightStudio.LightPlayer.Views;

public partial class InitialSetupView : UserControl
{
    private IInsetsManager? insetsManager;

    public InitialSetupView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this)?.InsetsManager is { } manager)
        {
            insetsManager = manager;
            manager.DisplayEdgeToEdgePreference = true;
            manager.SystemBarColor = Colors.Transparent;
            manager.SafeAreaChanged += OnSafeAreaChanged;
            UpdateSafeAreaPadding(manager.SafeAreaPadding);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (insetsManager is not null)
        {
            insetsManager.SafeAreaChanged -= OnSafeAreaChanged;
            insetsManager = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void OnSafeAreaChanged(object? sender, SafeAreaChangedArgs e) =>
        UpdateSafeAreaPadding(e.SafeAreaPadding);

    private void UpdateSafeAreaPadding(Thickness padding)
    {
        SetupRoot.Margin = padding;
        SetupFooter.Margin = new Thickness(-padding.Left, 0, -padding.Right, -padding.Bottom);
        var bottomSpacing = OperatingSystem.IsIOS() ? Math.Max(0, 14 - padding.Bottom) : 14;
        SetupFooter.Padding = new Thickness(32 + padding.Left, 14, 32 + padding.Right, bottomSpacing + padding.Bottom);
    }

    private async void OnLyricSourceInfoLinkClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { Tag: string url } ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            TopLevel.GetTopLevel(this)?.Launcher is not { } launcher)
        {
            return;
        }

        await launcher.LaunchUriAsync(uri);
    }
}
