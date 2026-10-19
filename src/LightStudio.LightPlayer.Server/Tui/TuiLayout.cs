using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace LightStudio.LightPlayer.Server.Tui;

/// <summary>A settings page of <see cref="ServerConfigurationTui"/>.</summary>
internal interface IConfigurationTab
{
    /// <summary>
    /// Copies what is on screen into <paramref name="config"/>, or returns why it cannot. Nothing is
    /// written to the configuration when a message is returned.
    /// </summary>
    string? Apply(ServerConfiguration config);
}

/// <summary>Row-based form layout shared by the settings pages.</summary>
internal static class TuiLayout
{
    public const int LabelWidth = 22;

    public static TextField Field(View parent, int row, string label, string value, bool secret = false)
    {
        parent.Add(new Label { X = 0, Y = row, Text = label });
        var field = new TextField
        {
            X = LabelWidth,
            Y = row,
            Width = Dim.Fill(),
            Text = value,
            Secret = secret,
        };
        parent.Add(field);
        return field;
    }

    public static CheckBox Check(View parent, int row, string label, bool value)
    {
        var check = new CheckBox
        {
            X = 0,
            Y = row,
            Text = label,
            Value = value ? CheckState.Checked : CheckState.UnChecked,
        };
        parent.Add(check);
        return check;
    }

    public static Label Note(View parent, int row, string text)
    {
        var note = new Label { X = 0, Y = row, Text = text };
        parent.Add(note);
        return note;
    }

    public static bool IsChecked(this CheckBox check) => check.Value == CheckState.Checked;
}

/// <summary>
/// A dropdown over a fixed set of stored values shown by their display names. The selection is read
/// back from the visible label, so the labels of one choice have to be distinct.
/// </summary>
internal sealed class TuiChoice
{
    private readonly ObservableCollection<string> labels = [];
    private readonly List<string> values = [];

    public TuiChoice(View parent, int row, string label, int width = 34)
    {
        Prompt = new Label { X = 0, Y = row, Text = label };
        List = new DropDownList
        {
            X = TuiLayout.LabelWidth,
            Y = row,
            Width = width,
            ReadOnly = true,
            Source = new ListWrapper<string>(labels),
        };

        parent.Add(Prompt, List);
    }

    public DropDownList List { get; }

    public Label Prompt { get; }

    /// <summary>The stored value behind the visible label, or the first option when none matches.</summary>
    public string Value
    {
        get
        {
            int index = labels.IndexOf(List.Text ?? string.Empty);
            return index >= 0 && index < values.Count ? values[index] : values.Count > 0 ? values[0] : string.Empty;
        }
    }

    public bool Visible
    {
        get => List.Visible;
        set
        {
            List.Visible = value;
            Prompt.Visible = value;
        }
    }

    public void SetOptions(IReadOnlyList<(string Value, string Label)> options, string selected)
    {
        labels.Clear();
        values.Clear();
        string selectedLabel = string.Empty;
        foreach ((string value, string label) in options)
        {
            labels.Add(label);
            values.Add(value);
            if (string.Equals(value, selected, StringComparison.OrdinalIgnoreCase))
            {
                selectedLabel = label;
            }
        }

        List.Text = selectedLabel.Length > 0 ? selectedLabel : labels.Count > 0 ? labels[0] : string.Empty;
    }
}
