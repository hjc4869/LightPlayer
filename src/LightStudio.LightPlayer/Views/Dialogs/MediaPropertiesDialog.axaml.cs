using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.Views.Dialogs;

/// <summary>Read-only media metadata viewer fed a flat list of key/value rows.</summary>
public partial class MediaPropertiesDialog : Window
{
    public MediaPropertiesDialog()
    {
        InitializeComponent();
        OkButton.Click += OnOk;
    }

    public MediaPropertiesDialog(string header)
        : this()
    {
        HeaderText.Text = header;
    }

    public MediaPropertiesDialog(string header, IReadOnlyList<MediaPropertyItem> properties)
        : this(header)
    {
        SetProperties(header, properties);
    }

    internal void SetProperties(string header, IReadOnlyList<MediaPropertyItem> properties)
    {
        HeaderText.Text = header;
        PropertiesList.ItemsSource = properties;
        LoadingProgressBar.IsIndeterminate = false;
        LoadingPanel.IsVisible = false;
        PropertiesScrollViewer.IsVisible = true;
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close();

    private async void OnCopyValueClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: MediaPropertyItem item } && Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(item.Value);
        }
    }
}
