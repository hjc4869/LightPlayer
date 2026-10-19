using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Services;

namespace LightStudio.LightPlayer.Desktop.Apple;

/// <summary>
/// macOS single-instance coordinator. The primary process claims a per-user
/// advisory lock file and listens on a Unix domain socket; secondary launches
/// forward their target-open or plain activation request over the socket and exit.
/// </summary>
/// <remarks>
/// <para>
/// A <c>.app</c> bundle launched through Finder is already single-instanced by
/// LaunchServices, but command-line launches (and <c>open</c> with fresh
/// arguments) still spawn independent processes. This service coordinates them the
/// same way the Windows (named pipe) and Linux (D-Bus name) implementations do,
/// using only the BCL so it stays NativeAOT-clean.
/// </para>
/// <para>
/// Primary election uses an exclusive lock file rather than the socket alone: on
/// Unix, <see cref="FileShare.None"/> maps to an advisory <c>flock</c>, so exactly
/// one process holds the lock and owns the socket node. Because the lock is tied to
/// the open file description, the OS releases it automatically if the primary
/// crashes, letting the next launch take over. Everything is best-effort; if the
/// socket cannot be created the process simply runs standalone rather than failing
/// to start.
/// </para>
/// <para>
/// Both the lock file and the socket live in the per-user temporary directory
/// (mode <c>0700</c>) and are further restricted to the owner, so another local
/// user cannot inject activation paths. The wire reader bounds every length so a
/// malformed or hostile client cannot exhaust memory. <see cref="FilesOpened"/> /
/// <see cref="Activated"/> are raised on the accept loop, so subscribers must
/// marshal to the UI thread.
/// </para>
/// </remarks>
internal sealed class MacSingleInstanceService : ISingleInstanceService
{
    private const uint ProtocolMagic = 0x4C50414D; // "MAPL" in little-endian byte order.
    private const byte ProtocolVersion = 1;
    private const int HeaderLength = sizeof(uint) + sizeof(byte) + sizeof(int);
    private const int MaxPathCount = 4096;
    private const int MaxPathBytes = 1024 * 1024;
    private const int MaxRequestBytes = 8 * 1024 * 1024;
    private const int ListenBacklog = 16;
    private const int ConnectTimeoutMilliseconds = 3000;
    private const int ClientReadTimeoutMilliseconds = 5000;
    private const int ElectionAttempts = 10;
    private const int ElectionRetryDelayMilliseconds = 100;

    private const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly string socketPath;
    private readonly string lockPath;
    private readonly object gate = new();
    private readonly List<IReadOnlyList<string>> pendingFileRequests = new();

    private Action<IReadOnlyList<string>>? filesOpened;
    private Action? activated;
    private int pendingActivations;
    private FileStream? lockFile;
    private Socket? listener;
    private CancellationTokenSource? listenerCts;
    private Task? listenerTask;
    private bool disposed;

    public MacSingleInstanceService(string appId)
    {
        var directory = Path.GetTempPath();
        socketPath = Path.Combine(directory, $"{appId}.sock");
        lockPath = Path.Combine(directory, $"{appId}.lock");
    }

    public event Action<IReadOnlyList<string>>? FilesOpened
    {
        add
        {
            IReadOnlyList<string>[] pending;
            lock (gate)
            {
                filesOpened += value;
                pending = pendingFileRequests.ToArray();
                pendingFileRequests.Clear();
            }

            if (value is not null)
            {
                foreach (var paths in pending)
                {
                    value(paths);
                }
            }
        }
        remove
        {
            lock (gate)
            {
                filesOpened -= value;
            }
        }
    }

    public event Action? Activated
    {
        add
        {
            int pending;
            lock (gate)
            {
                activated += value;
                pending = pendingActivations;
                pendingActivations = 0;
            }

            if (value is not null)
            {
                for (var i = 0; i < pending; i++)
                {
                    value();
                }
            }
        }
        remove
        {
            lock (gate)
            {
                activated -= value;
            }
        }
    }

    public bool TryActivate(IReadOnlyList<string> activationPaths)
    {
        if (disposed)
        {
            return true;
        }

        for (var attempt = 0; attempt < ElectionAttempts; attempt++)
        {
            var claimed = TryAcquireLock();
            if (claimed is not null)
            {
                // We hold the lock, so no live primary owns the socket node. Start
                // serving; on any failure run standalone rather than block startup.
                try
                {
                    StartListening(claimed);
                    return true;
                }
                catch
                {
                    claimed.Dispose();
                    return true;
                }
            }

            // The lock is held: a primary exists (or is mid-startup). Forward the
            // activation to it and exit once it accepts the request.
            if (TryForwardToPrimary(activationPaths))
            {
                return false;
            }

            // The primary may not have started listening yet, or it died after
            // taking the lock (which frees it). Back off briefly and retry: the next
            // pass either connects to the now-ready primary or claims the freed lock.
            Thread.Sleep(ElectionRetryDelayMilliseconds);
        }

        // Could not coordinate within the retry budget: act as our own primary.
        return true;
    }

    private FileStream? TryAcquireLock()
    {
        try
        {
            var stream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            TrySetOwnerOnly(lockPath);
            return stream;
        }
        catch (IOException)
        {
            // Exclusive share (advisory flock) is held by another instance.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void StartListening(FileStream claimedLock)
    {
        // We own the lock, so any leftover socket node is stale (a previous crash);
        // remove it before binding so bind() does not fail with EADDRINUSE.
        TryDeleteSocketFile();

        var server = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            server.Bind(new UnixDomainSocketEndPoint(socketPath));
            server.Listen(ListenBacklog);
            TrySetOwnerOnly(socketPath);
        }
        catch
        {
            server.Dispose();
            throw;
        }

        var cts = new CancellationTokenSource();
        lockFile = claimedLock;
        listener = server;
        listenerCts = cts;
        listenerTask = Task.Run(() => AcceptLoopAsync(server, cts.Token));
    }

    private bool TryForwardToPrimary(IReadOnlyList<string> activationPaths)
    {
        try
        {
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
            {
                SendTimeout = ConnectTimeoutMilliseconds,
            };
            client.Connect(new UnixDomainSocketEndPoint(socketPath));
            using var stream = new NetworkStream(client, ownsSocket: false);
            WriteRequest(stream, activationPaths);
            stream.Flush();
            return true;
        }
        catch
        {
            // No listener yet, connection refused, or the primary went away: the
            // caller retries (and may become the primary itself).
            return false;
        }
    }

    private async Task AcceptLoopAsync(Socket server, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await server.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch
            {
                // Transient accept failure: keep serving later launches.
                continue;
            }

            try
            {
                using (client)
                {
                    // Bound the read so a slow or stuck client cannot stall the loop
                    // (activations are handled one at a time).
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    readCts.CancelAfter(ClientReadTimeoutMilliseconds);
                    using var stream = new NetworkStream(client, ownsSocket: false);
                    var paths = await ReadRequestAsync(stream, readCts.Token).ConfigureAwait(false);
                    if (paths is not null)
                    {
                        Dispatch(paths);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // A malformed, slow, or prematurely closed client must not stop
                // later activations from reaching the primary instance.
            }
        }
    }

    private void Dispatch(IReadOnlyList<string> paths)
    {
        if (paths.Count > 0)
        {
            Action<IReadOnlyList<string>>? handler;
            lock (gate)
            {
                handler = filesOpened;
                if (handler is null)
                {
                    pendingFileRequests.Add(paths);
                }
            }

            handler?.Invoke(paths);
            return;
        }

        Action? activationHandler;
        lock (gate)
        {
            activationHandler = activated;
            if (activationHandler is null)
            {
                pendingActivations++;
            }
        }

        activationHandler?.Invoke();
    }

    private static void WriteRequest(Stream stream, IReadOnlyList<string> paths)
    {
        if (paths.Count > MaxPathCount)
        {
            throw new InvalidDataException("Too many activation paths.");
        }

        Span<byte> header = stackalloc byte[HeaderLength];
        BinaryPrimitives.WriteUInt32LittleEndian(header, ProtocolMagic);
        header[sizeof(uint)] = ProtocolVersion;
        BinaryPrimitives.WriteInt32LittleEndian(header[(sizeof(uint) + sizeof(byte))..], paths.Count);
        stream.Write(header);

        var totalBytes = HeaderLength;
        Span<byte> length = stackalloc byte[sizeof(int)];
        foreach (var path in paths)
        {
            var byteCount = Encoding.UTF8.GetByteCount(path);
            totalBytes += sizeof(int) + byteCount;
            if (byteCount > MaxPathBytes || totalBytes > MaxRequestBytes)
            {
                throw new InvalidDataException("Activation request is too large.");
            }

            BinaryPrimitives.WriteInt32LittleEndian(length, byteCount);
            stream.Write(length);

            var bytes = Encoding.UTF8.GetBytes(path);
            stream.Write(bytes, 0, bytes.Length);
        }
    }

    private static async Task<IReadOnlyList<string>?> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var header = new byte[HeaderLength];
        if (!await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false) ||
            BinaryPrimitives.ReadUInt32LittleEndian(header) != ProtocolMagic ||
            header[sizeof(uint)] != ProtocolVersion)
        {
            return null;
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(
            header.AsSpan(sizeof(uint) + sizeof(byte), sizeof(int)));
        if (count is < 0 or > MaxPathCount)
        {
            return null;
        }

        var paths = new string[count];
        var lengthBuffer = new byte[sizeof(int)];
        var totalBytes = HeaderLength;
        for (var i = 0; i < count; i++)
        {
            if (!await ReadExactlyAsync(stream, lengthBuffer, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            var byteCount = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
            totalBytes += sizeof(int) + byteCount;
            if (byteCount is < 0 or > MaxPathBytes || totalBytes > MaxRequestBytes)
            {
                return null;
            }

            var bytes = new byte[byteCount];
            if (!await ReadExactlyAsync(stream, bytes, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            paths[i] = Encoding.UTF8.GetString(bytes);
        }

        return paths;
    }

    private static async Task<bool> ReadExactlyAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(offset, buffer.Length - offset),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    private static void TrySetOwnerOnly(string path)
    {
        try
        {
#pragma warning disable CA1416 // Validate platform compatibility
            File.SetUnixFileMode(path, OwnerReadWrite);
#pragma warning restore CA1416 // Validate platform compatibility
        }
        catch
        {
            // Best-effort hardening; the per-user temp directory already restricts
            // access to the owner.
        }
    }

    private void TryDeleteSocketFile()
    {
        try
        {
            File.Delete(socketPath);
        }
        catch
        {
            // Nothing to remove, or a permission error we cannot resolve; bind will
            // surface any real problem.
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? cts;
        Socket? server;
        FileStream? claimedLock;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            cts = listenerCts;
            listenerCts = null;
            server = listener;
            listener = null;
            claimedLock = lockFile;
            lockFile = null;
            filesOpened = null;
            activated = null;
            pendingFileRequests.Clear();
            pendingActivations = 0;
        }

        cts?.Cancel();
        cts?.Dispose();
        server?.Dispose();
        if (claimedLock is not null)
        {
            // Remove the socket node before releasing the lock so the next launch
            // does not observe a stale node while it owns the freed lock.
            TryDeleteSocketFile();
            claimedLock.Dispose();
        }

        listenerTask = null;
    }
}
