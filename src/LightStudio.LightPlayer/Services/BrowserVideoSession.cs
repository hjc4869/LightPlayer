using System;
using LightMediaRenderer.Avalonia.Pipeline;

namespace LightStudio.LightPlayer.Services;

public enum BrowserElementCommandKind
{
    Play,
    Pause,
    Seek,
    Rate,
    Volume,
}

/// <summary>A media element call made in the page, numbered in the order the page made it.</summary>
/// <param name="Value">Seek target in seconds, rate, or volume from 0 to 1.</param>
public readonly record struct BrowserElementCommand(
    long Number,
    BrowserElementCommandKind Kind,
    double Value = 0,
    bool Muted = false);

/// <summary>The player's state as the page's element reports it.</summary>
public sealed record BrowserElementState
{
    /// <summary>Number of the last command applied, 0 before any.</summary>
    public long Ack { get; init; }

    public double Time { get; init; }

    public bool Paused { get; init; } = true;

    public double Rate { get; init; } = 1;

    public double Volume { get; init; } = 1;

    public bool Muted { get; init; }

    /// <summary>HTML <c>readyState</c>, 0 to 4.</summary>
    public int ReadyState { get; init; }

    public bool Seeking { get; init; }

    public bool Ended { get; init; }

    /// <summary>Length in seconds, or null while unknown or unbounded.</summary>
    public double? Duration { get; init; }

    public int VideoWidth { get; init; }

    public int VideoHeight { get; init; }

    public long TotalFrames { get; init; }

    public long DroppedFrames { get; init; }

    public double ViewWidth { get; init; }

    public double ViewHeight { get; init; }

    public double DevicePixelRatio { get; init; } = 1;
}

public enum BrowserSessionEndReason
{
    Stopped,
    Replaced,
    Unauthorized,
    Encrypted,
    Unsupported,
    Error,
}

/// <summary>A video a browser extension redirected to this app, for as long as the redirect lasts.</summary>
public interface IBrowserVideoSession
{
    /// <summary>The plain file the page plays, with its position and request headers; null when the page pushes the media.</summary>
    StreamLink? File { get; }

    /// <summary>
    /// Hands over the media the page pushes into its element, which the caller then owns. Null for a
    /// plain file, once taken, and after the session ended.
    /// </summary>
    AppendMediaSourceProvider? TakeSource();

    /// <summary>The page's title, or null when it has none.</summary>
    string? Title { get; }

    double StartSeconds { get; }

    bool StartPaused { get; }

    double StartRate { get; }

    /// <summary>
    /// The number of the last element command the page sent before the start members took effect;
    /// a newer command overrides the member it concerns.
    /// </summary>
    long StartCommand { get; }

    bool IsEnded { get; }

    /// <summary>Raised on the UI thread for each element command, in command order.</summary>
    event EventHandler<BrowserElementCommand>? CommandReceived;

    /// <summary>Raised on the UI thread when the browser or a newer session ended this one.</summary>
    event EventHandler<BrowserSessionEndReason>? Ended;

    /// <summary>
    /// Raised on the UI thread when the page attached a new source to its element: the media starts
    /// again empty, and the start members describe the element at that point.
    /// </summary>
    event EventHandler? SourceReset;

    /// <summary>
    /// Raised on the UI thread when the new source carries other kinds of tracks than the one playback
    /// was set up with: the media moved to another provider, which <see cref="TakeSource"/> hands over.
    /// </summary>
    event EventHandler? SourceReplaced;

    /// <summary>Hands the browser the player's state; repeats of the last state are not sent.</summary>
    void ReportState(BrowserElementState state);

    /// <summary>Ends the session from the app's side and tells the browser why.</summary>
    void End(BrowserSessionEndReason reason, string? message = null);
}
