using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Services;

namespace LightStudio.LightPlayer.Desktop.Windows;

/// <summary>
/// Windows single-instance coordinator. The primary process keeps a named mutex
/// alive and listens on a current-user named pipe; secondary launches forward
/// their target-open or plain activation request and exit.
/// </summary>
/// <remarks>
/// The mutex is local to the interactive Windows session, matching desktop app
/// semantics. The session id is also included in the machine-wide named-pipe
/// namespace, while <see cref="PipeOptions.CurrentUserOnly"/> prevents another
/// user in that session from injecting activation paths. All IPC is best-effort:
/// if Windows IPC is unavailable, the process starts independently instead of
/// failing to launch.
/// </remarks>
internal sealed class WindowsSingleInstanceService : ISingleInstanceService
{
    private const uint ProtocolMagic = 0x4341504C; // "LPAC" in little-endian byte order.
    private const byte ProtocolVersion = 1;
    private const int HeaderLength = sizeof(uint) + sizeof(byte) + sizeof(int);
    private const int ConnectTimeoutMilliseconds = 3000;
    private const int MaxPathCount = 4096;
    private const int MaxPathBytes = 1024 * 1024;
    private const int MaxRequestBytes = 8 * 1024 * 1024;

    private readonly string mutexName;
    private readonly string pipeName;
    private readonly object gate = new();
    private readonly List<IReadOnlyList<string>> pendingFileRequests = new();

    private Action<IReadOnlyList<string>>? filesOpened;
    private Action? activated;
    private int pendingActivations;
    private Mutex? instanceMutex;
    private CancellationTokenSource? listenerCts;
    private Task? listenerTask;
    private bool disposed;

    public WindowsSingleInstanceService(string appId)
    {
        mutexName = $"Local\\{appId}.SingleInstance";
        pipeName = $"{appId}.{Process.GetCurrentProcess().SessionId}.Activation";
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

        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(initiallyOwned: false, mutexName, out var createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                mutex = null;
                return !ForwardToPrimary(activationPaths);
            }

            // Create the first server instance before returning so a process that
            // observes the mutex immediately can connect while the UI is starting.
            var firstServer = CreateServer();
            var cts = new CancellationTokenSource();
            instanceMutex = mutex;
            listenerCts = cts;
            listenerTask = Task.Run(() => ListenAsync(firstServer, cts.Token));
            return true;
        }
        catch
        {
            mutex?.Dispose();
            instanceMutex = null;
            listenerCts?.Cancel();
            listenerCts?.Dispose();
            listenerCts = null;
            return true;
        }
    }

    private bool ForwardToPrimary(IReadOnlyList<string> activationPaths)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                serverName: ".",
                pipeName,
                PipeDirection.Out,
                PipeOptions.CurrentUserOnly);
            client.Connect(ConnectTimeoutMilliseconds);
            WriteRequest(client, activationPaths);
            client.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task ListenAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using (server)
                {
                    await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    var paths = await ReadRequestAsync(server, cancellationToken).ConfigureAwait(false);
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
                // A malformed or prematurely disconnected client must not stop
                // later activations from reaching the primary instance.
                server.Dispose();
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                server = CreateServer();
            }
            catch
            {
                return;
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

    private NamedPipeServerStream CreateServer() => new(
        pipeName,
        PipeDirection.In,
        maxNumberOfServerInstances: 1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
        inBufferSize: 4096,
        outBufferSize: 0);

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

    public void Dispose()
    {
        CancellationTokenSource? cts;
        Mutex? mutex;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            cts = listenerCts;
            listenerCts = null;
            mutex = instanceMutex;
            instanceMutex = null;
            filesOpened = null;
            activated = null;
            pendingFileRequests.Clear();
            pendingActivations = 0;
        }

        cts?.Cancel();
        cts?.Dispose();
        mutex?.Dispose();
        listenerTask = null;
    }
}