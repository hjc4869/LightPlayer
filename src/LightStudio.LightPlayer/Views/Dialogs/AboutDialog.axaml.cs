using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LightStudio.LightPlayer.Views.Dialogs;

/// <summary>Read-only about box: product name, version, and a short description.</summary>
public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0.0";
        VersionText.Text = $"Version {version}";
        OkButton.Click += OnOk;
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close();
}
