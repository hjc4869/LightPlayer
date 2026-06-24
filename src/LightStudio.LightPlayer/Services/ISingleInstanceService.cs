using System;
using System.Collections.Generic;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Enforces a single running instance and routes file-open / activation requests
/// from secondary launches to the primary instance.
/// </summary>
/// <remarks>
/// The desktop launches a fresh process for every "Open with" / command-line
/// invocation (this is always the case under Flatpak). <see cref="TryActivate"/>
/// lets the first process become the primary owner; later processes forward their
/// activation to it and exit, so the user keeps a single window whose queue is
/// replaced by the newly opened files.
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
    /// Raised on the primary instance when a secondary instance opens files. Paths
    /// are already resolved to local paths. May be raised on a background thread.
    /// </summary>
    event Action<IReadOnlyList<string>>? FilesOpened;

    /// <summary>
    /// Raised on the primary instance when a secondary instance requests activation
    /// with no files (e.g. re-launching the app). May be raised on a background thread.
    /// </summary>
    event Action? Activated;
}

/// <summary>
/// Default no-op implementation for platforms without single-instance IPC wired
/// in. Every process becomes its own primary, matching the prior behavior.
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
