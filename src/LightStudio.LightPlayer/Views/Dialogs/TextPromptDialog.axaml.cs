using System;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace LightStudio.LightPlayer.Views.Dialogs;

/// <summary>
/// A minimal text prompt. Closes with the trimmed text, or null when canceled.
/// </summary>
public partial class TextPromptDialog : Window
{
    private Func<string, bool>? validation;
    private string? validationMessage;

    public bool AcceptsReturn
    {
        get => InputBox.AcceptsReturn;
        set
        {
            InputBox.AcceptsReturn = value;
            InputBox.Height = value ? 120 : double.NaN;
            InputBox.MaxWidth = value ? 520 : double.PositiveInfinity;
            InputBox.MaxHeight = value ? 120 : double.PositiveInfinity;
            InputBox.TextWrapping = global::Avalonia.Media.TextWrapping.NoWrap;
            ScrollViewer.SetHorizontalScrollBarVisibility(
                InputBox,
                value ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(
                InputBox,
                value ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
            ConfirmButton.IsDefault = !value;
        }
    }

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

    public TextPromptDialog(
        string header,
        string confirmLabel,
        string? initialValue,
        Func<string, bool> validation,
        string validationMessage,
        string? watermark = null)
        : this(header, confirmLabel, initialValue)
    {
        this.validation = validation;
        this.validationMessage = validationMessage;
        InputBox.PlaceholderText = watermark;
    }

    protected override void OnOpened(System.EventArgs e)
    {
        base.OnOpened(e);
        InputBox.SelectAll();
        InputBox.Focus();
    }

    private void OnInputKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (!AcceptsReturn || e.KeyModifiers.HasFlag(KeyModifiers.Control)))
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

    private void OnInputTextChanged(object? sender, TextChangedEventArgs e)
    {
        ValidationText.IsVisible = false;
        UpdateConfirmState();
    }

    private void Confirm()
    {
        string value = InputBox.Text?.Trim() ?? string.Empty;
        if (value.Length == 0)
            return;
        if (validation?.Invoke(value) == false)
        {
            ValidationText.Text = validationMessage;
            ValidationText.IsVisible = true;
            return;
        }

        Close(value);
    }

    private void UpdateConfirmState() => ConfirmButton.IsEnabled = !string.IsNullOrWhiteSpace(InputBox.Text);
}
