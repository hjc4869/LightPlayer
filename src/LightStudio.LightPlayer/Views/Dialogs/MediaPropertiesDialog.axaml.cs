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

    public MediaPropertiesDialog(string header, IReadOnlyList<MediaPropertyItem> properties)
        : this()
    {
        HeaderText.Text = header;
        PropertiesList.ItemsSource = properties;
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
