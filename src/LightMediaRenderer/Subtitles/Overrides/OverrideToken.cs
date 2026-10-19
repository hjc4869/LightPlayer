using LightMediaRenderer.Contracts;
namespace LightMediaRenderer.Subtitles.Overrides;

internal enum OverrideTokenKind
{
    /// <summary>Literal characters to draw.</summary>
    Text,

    /// <summary>A single <c>\tag</c> with its raw argument.</summary>
    Tag,

    /// <summary>An explicit <c>\N</c> or <c>\n</c>.</summary>
    LineBreak,
}

/// <summary>
/// One item produced by <see cref="OverrideParser"/>.
/// </summary>
internal readonly struct OverrideToken
{
    private OverrideToken(OverrideTokenKind kind, string name, string value, bool hard)
    {
        Kind = kind;
        Name = name;
        Value = value;
        IsHardBreak = hard;
    }

    public OverrideTokenKind Kind { get; }

    /// <summary>Lower-case tag name without the backslash; empty for text.</summary>
    public string Name { get; }

    /// <summary>Literal text, or the raw tag argument.</summary>
    public string Value { get; }

    /// <summary><c>\N</c> always breaks; <c>\n</c> only does under wrap style 2.</summary>
    public bool IsHardBreak { get; }

    public static OverrideToken Text(string value) => new(OverrideTokenKind.Text, string.Empty, value, false);

    public static OverrideToken Tag(string name, string value) => new(OverrideTokenKind.Tag, name, value, false);

    public static OverrideToken LineBreak(bool hard) => new(OverrideTokenKind.LineBreak, string.Empty, string.Empty, hard);
}
