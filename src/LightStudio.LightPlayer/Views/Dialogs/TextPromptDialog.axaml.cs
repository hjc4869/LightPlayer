using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace LightStudio.LightPlayer.Views.Dialogs;

/// <summary>
/// A minimal single-line text prompt used to name and rename playlists. Closes
/// with the trimmed text, or null when cancelled.
/// </summary>
public partial class TextPromptDialog : Window
{
    public TextPromptDialog()
    {
        InitializeComponent();
        ConfirmButton.Click += OnConfirm;
        CancelButton.Click += OnCancel;
        InputBox.KeyUp += OnInputKeyUp;
        InputBox.TextChanged += OnInputTextChanged;
    }

    public TextPromptDialog(string header, string confirmLabel, string? initialValue)
        : this()
    {
        HeaderText.Text = header;
        ConfirmButton.Content = confirmLabel;
        InputBox.Text = initialValue ?? string.Empty;
        UpdateConfirmState();
    }

    protected override void OnOpened(System.EventArgs e)
    {
        base.OnOpened(e);
        InputBox.SelectAll();
        InputBox.Focus();
    }

    private void OnInputKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && IsValid())
        {
            Confirm();
        }
        else if (e.Key == Key.Escape)
        {
            Close(null);
        }
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Confirm();

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnInputTextChanged(object? sender, TextChangedEventArgs e) => UpdateConfirmState();

    private void Confirm()
    {
        if (IsValid())
        {
            Close(InputBox.Text!.Trim());
        }
    }

    private bool IsValid() => !string.IsNullOrWhiteSpace(InputBox.Text);

    private void UpdateConfirmState() => ConfirmButton.IsEnabled = IsValid();
}
