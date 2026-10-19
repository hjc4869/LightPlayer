using System;
using System.Collections.Generic;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Enforces a single running instance and routes launch activation requests from
/// secondary launches to the primary instance.
/// </summary>
/// <remarks>
/// The desktop launches a fresh process for every "Open with" / command-line
/// invocation. <see cref="TryActivate"/> lets the first process become the primary owner;
/// later processes forward their
/// activation to it and exit, so the user keeps a single window for local files
/// and stream links.
/// </remarks>
public interface ISingleInstanceService : IDisposable
{
    /// <summary>
    /// Attempts to become the primary instance. Returns <c>true</c> for the
    /// primary (the caller should keep starting up). Returns <c>false</c> when an
    /// existing primary handled <paramref name="activationPaths"/> and the caller
    /// should exit. Never throws; falls back to acting as the primary when no IPC
    /// channel (session bus) is available.
    /// </summary>
    bool TryActivate(IReadOnlyList<string> activationPaths);

    /// <summary>
    /// Raised on the primary instance when a secondary instance opens targets. Values
    /// are resolved local paths or normalized HTTP(S) URLs. May be raised on a background thread.
    /// </summary>
    event Action<IReadOnlyList<string>>? FilesOpened;

    /// <summary>
    /// Raised on the primary instance when a secondary instance requests activation
    /// with no files (e.g. re-launching the app). May be raised on a background thread.
    /// </summary>
    event Action? Activated;
}

/// <summary>
/// Default no-op implementation for platforms without single-instance IPC.
/// </summary>
public sealed class NoopSingleInstanceService : ISingleInstanceService
{
    public event Action<IReadOnlyList<string>>? FilesOpened
    {
        add { }
        remove { }
    }

    public event Action? Activated
    {
        add { }
        remove { }
    }

    public bool TryActivate(IReadOnlyList<string> activationPaths) => true;

    public void Dispose()
    {
    }
}
