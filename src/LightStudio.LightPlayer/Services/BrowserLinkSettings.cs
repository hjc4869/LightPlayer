using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LightStudio.LightPlayer.Services;

/// <summary>A browser extension the user allowed to send videos to this device.</summary>
/// <param name="Label">Browser name the extension reported when it paired.</param>
/// <param name="Origin">Origin of the extension's background context, such as <c>chrome-extension://id</c>.</param>
/// <param name="TokenVerifier">A verifier of the token issued at pairing; never the token itself.</param>
public sealed record BrowserLinkPairing(string Label, string Origin, string TokenVerifier, long CreatedAtUnixSeconds);

/// <summary>Persisted configuration of the desktop head's browser link listener.</summary>
public sealed record BrowserLinkSettings
{
    public const int DefaultPort = 47833;

    public static BrowserLinkSettings Default { get; } = new();

    public bool Enabled { get; init; }

    public int Port { get; init; } = DefaultPort;

    public IReadOnlyList<BrowserLinkPairing> PairedBrowsers { get; init; } = [];

    public bool Equals(BrowserLinkSettings? other) =>
        other is not null &&
        Enabled == other.Enabled &&
        Port == other.Port &&
        PairedBrowsers.SequenceEqual(other.PairedBrowsers);

    public override int GetHashCode() => HashCode.Combine(Enabled, Port, PairedBrowsers.Count);
}

public sealed record BrowserLinkState(bool IsRunning, int Port, string ErrorMessage)
{
    public static BrowserLinkState Stopped { get; } = new(false, 0, string.Empty);
}

/// <summary>What a browser extension asks for when it pairs.</summary>
/// <param name="Origin">The handshake's <c>Origin</c> header.</param>
/// <param name="Code">Digits the extension shows, so the user can tell the request is theirs.</param>
public sealed record BrowserLinkPairingRequest(string Origin, string Code, string Browser, string ExtensionVersion);

/// <summary>The parts of the app's UI the browser link listener needs. Called on the UI thread.</summary>
public interface IBrowserLinkPresenter
{
    /// <summary>
    /// Asks the user whether to pair the extension; false when declined or unanswerable. The prompt
    /// closes as declined once <paramref name="cancellationToken"/> fires.
    /// </summary>
    Task<bool> ConfirmPairingAsync(BrowserLinkPairingRequest request, CancellationToken cancellationToken);

    /// <summary>Plays a redirected video in place of the current video session.</summary>
    Task OpenSessionAsync(IBrowserVideoSession session);
}

public interface IBrowserLinkController
{
    BrowserLinkState State { get; }

    /// <summary>Raised on the UI thread whenever <see cref="State"/> changes.</summary>
    event EventHandler? StateChanged;

    /// <summary>Set by the shell once it can show dialogs and play video.</summary>
    IBrowserLinkPresenter? Presenter { get; set; }

    /// <summary>Starts, stops or moves the listener, and ends sessions of revoked browsers. Does not block.</summary>
    void Apply(BrowserLinkSettings settings);
}
