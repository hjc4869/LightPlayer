using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using LightStudio.FfmpegShim.Decoding;
using LightStudio.FfmpegShim.Transcoding;
using LightStudio.LightPlayer.Services;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.IO;

namespace LightStudio.RemoteApi;

/// <summary>
/// Starts, stops and restarts a <see cref="RemoteApiServer"/> as <see cref="RemoteApiSettings"/>
/// change, and reports the resulting state so the UI can show it live.
/// </summary>
public sealed class RemoteApiServerController : IRemoteApiServerController, IRemoteApiPairingController, IDisposable
{
    private readonly object gate = new();
    private readonly ILocalServices services;
    private readonly IMultiMediaPlatformIO multimediaPlatform;
    private readonly string? staticFilesPath;
    private readonly Func<HardwareDecoderSettings>? hardwareSettings;
    private readonly byte[]? tokenSecret;
    private RemoteApiServer? server;
    private RemoteApiSettings appliedSettings = RemoteApiSettings.Disabled;
    private RemoteApiServerState state = RemoteApiServerState.Stopped;

    /// <param name="staticFilesPath">
    /// Directory holding the browser client. The desktop head passes null: it serves the API only
    /// and the client is loaded from wherever the user hosts it.
    /// </param>
    /// <param name="hardwareSettings">
    /// The per-device arguments the head's own hardware profiles resolved. Encoding reads the same
    /// ones decoding does, so a device opened one way is opened the other way too.
    /// </param>
    /// <param name="tokenSecret">Persisted secret from <see cref="RemoteApiTokenSecret"/> that signs session tokens.</param>
    public RemoteApiServerController(
        ILocalServices services,
        IMultiMediaPlatformIO multimediaPlatform,
        string? staticFilesPath = null,
        Func<HardwareDecoderSettings>? hardwareSettings = null,
        byte[]? tokenSecret = null)
    {
        this.services = services;
        this.multimediaPlatform = multimediaPlatform;
        this.staticFilesPath = staticFilesPath;
        this.hardwareSettings = hardwareSettings;
        this.tokenSecret = tokenSecret;
    }

    public event EventHandler? StateChanged;

    public RemoteApiServerState State
    {
        get
        {
            lock (gate)
            {
                return state;
            }
        }
    }

    public RemoteApiServerState Apply(RemoteApiSettings settings)
    {
        RemoteApiServerState result;
        var changed = false;
        lock (gate)
        {
            var previous = state;
            result = ApplyCore(settings);
            changed = previous != result;
        }

        if (changed)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        return result;
    }

    public RemoteApiPairingInfo CreatePairing()
    {
        lock (gate)
        {
            if (server is null || !state.IsRunning)
            {
                throw new InvalidOperationException("The remote API server is not running.");
            }

            if (!string.Equals(appliedSettings.Host.Trim(), "0.0.0.0", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Pairing requires the server to listen on 0.0.0.0.");
            }

            var address = FindPrivateIpv4Address()
                ?? throw new InvalidOperationException("No private IPv4 network address is available for pairing.");
            var (token, expiresAt) = server.IssuePairingToken();
            return new RemoteApiPairingInfo(
                $"http://{address}:{appliedSettings.Port}",
                token,
                expiresAt);
        }
    }

    private static IPAddress? FindPrivateIpv4Address()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(IPAddress.Parse("192.0.2.1"), 9);
            if (socket.LocalEndPoint is IPEndPoint endpoint && IsPrivateIpv4(endpoint.Address))
            {
                return endpoint.Address;
            }
        }
        catch (SocketException)
        {
        }

        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(network =>
                network.OperationalStatus == OperationalStatus.Up &&
                network.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
            .Select(network => network.GetIPProperties())
            .Where(properties => properties.GatewayAddresses.Any(gateway =>
                gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
                !gateway.Address.Equals(IPAddress.Any)))
            .SelectMany(properties => properties.UnicastAddresses)
            .Select(address => address.Address)
            .FirstOrDefault(IsPrivateIpv4);
    }

    private static bool IsPrivateIpv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
            (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168);
    }

    private RemoteApiServerState ApplyCore(RemoteApiSettings settings)
    {
        AppLog.Information(
            "RemoteApi.Controller",
            $"Applying enabled={settings.Enabled}; host={settings.Host}; port={settings.Port}; " +
            $"authentication={settings.RequireAuthentication}; " +
            $"allowedOrigins={(settings.AllowedOrigins.Length == 0 ? "none" : settings.AllowedOrigins)}; " +
            $"transcoding={settings.TranscodeEnabled}; " +
            $"hardware={settings.TranscodeHardware}; hardwareScaling={settings.TranscodeHardwareScaling}; " +
            $"vaapiRateControl={settings.TranscodeVaapiRateControl}; vaapiQuality={settings.TranscodeVaapiQuality}; " +
            $"softwareFallback={settings.TranscodeAllowSoftware}; encoderInterface={settings.TranscodeDeviceType}; " +
            $"device={settings.TranscodeDevice}.");
        if (!settings.Enabled)
        {
            Stop();
            appliedSettings = settings;
            return state = RemoteApiServerState.Stopped;
        }

        if (!TryBuildPrefix(settings, out var listenerPrefix, out var displayAddress, out var error))
        {
            Stop();
            appliedSettings = settings;
            return state = new RemoteApiServerState(false, string.Empty, error);
        }

        if (settings.RequireAuthentication && settings.Credentials is null)
        {
            Stop();
            appliedSettings = settings;
            return state = new RemoteApiServerState(
                false,
                string.Empty,
                "Set a user name and password before requiring sign-in.");
        }

        if (server is not null && settings == appliedSettings)
        {
            return state;
        }

        Stop();
        try
        {
            var next = new RemoteApiServer(services, multimediaPlatform, new RemoteApiHostOptions
            {
                ListenerPrefix = listenerPrefix,
                StaticFilesPath = staticFilesPath,
                Credentials = settings.Credentials,
                TokenSecret = tokenSecret,
                AllowedOrigins = CorsOriginPolicy.Split(settings.AllowedOrigins),
                Transcoding = ResolveTranscoding(settings),
            });
            next.Start();
            server = next;
            appliedSettings = settings;
            state = new RemoteApiServerState(true, displayAddress, string.Empty);
            AppLog.Information("RemoteApi.Controller", $"Remote API started at {displayAddress}.");
        }
        catch (Exception ex)
        {
            AppLog.Error("RemoteApi.Controller", $"Remote API failed to start at {displayAddress}.", ex);
            state = new RemoteApiServerState(false, displayAddress, ex.GetBaseException().Message);
        }

        return state;
    }

    private void Stop()
    {
        if (server is not null)
        {
            AppLog.Information("RemoteApi.Controller", "Stopping the active remote API server.");
        }

        server?.Dispose();
        server = null;
    }

    /// <summary>
    /// Turns what the user chose into the arguments the encode path passes to FFmpeg. The device is
    /// keyed by device type exactly as the decode path keys its own, so one settings shape serves
    /// both and an encoder-only argument can be added later without disturbing either.
    /// </summary>
    private TranscodeOptions ResolveTranscoding(RemoteApiSettings settings)
    {
        if (!settings.TranscodeEnabled)
        {
            return TranscodeOptions.Disabled;
        }

        HardwareDecoderSettings resolved = hardwareSettings?.Invoke() ?? HardwareDecoderSettings.Empty;

        // The encoder's device is chosen apart from the decoder's, because the machine playing for
        // itself and the machine encoding for a client need not be using the same adapter.
        string encoderInterface = TranscodeEncoderInterfaces.NormalizeName(settings.TranscodeDeviceType);
        TranscodeEncoderInterface? encoder = TranscodeEncoderInterfaces.Find(encoderInterface);
        if (encoder is null && settings.TranscodeDevice.Length > 0)
        {
            var preferred = TranscodeEncoderInterfaces.GetPreferredForCurrentPlatform();
            encoder = preferred.Count > 0 ? preferred[0] : null;
        }

        if (encoder is not null)
        {
            resolved = resolved.WithDevice(encoder.DeviceType, settings.TranscodeDevice);
        }

        resolved = VaapiEncoderOptions.Apply(
            resolved,
            settings.TranscodeVaapiRateControl,
            settings.TranscodeVaapiQuality);
        resolved = MediaFoundationEncoderOptions.Apply(
            resolved,
            settings.TranscodeMediaFoundationRateControl,
            settings.TranscodeMediaFoundationQuality,
            settings.TranscodeMediaFoundationScenario);
        resolved = AmfEncoderOptions.Apply(
            resolved,
            settings.TranscodeAmfRateControl,
            settings.TranscodeAmfQuality,
            settings.TranscodeAmfUsage);
        resolved = NvencEncoderOptions.Apply(
            resolved,
            settings.TranscodeNvencRateControl,
            settings.TranscodeNvencPreset,
            settings.TranscodeNvencTune);
        resolved = QsvEncoderOptions.Apply(
            resolved,
            settings.TranscodeQsvPreset,
            settings.TranscodeQsvScenario);

        return new TranscodeOptions
        {
            Enabled = true,
            EnableHardware = settings.TranscodeHardware,
            EnableHardwareScaling = settings.TranscodeHardwareScaling,
            AllowSoftwareEncoding = settings.TranscodeAllowSoftware,
            HardwareEncoder = encoder?.Name,
            HardwareSettings = resolved,
        };
    }

    /// <summary>
    /// Turns a host/port pair into an <c>HttpListener</c> prefix plus a human-readable address.
    /// The wildcard listener host is required for "any address" bindings; the display address keeps
    /// the literal the user typed.
    /// </summary>
    public static bool TryBuildPrefix(
        RemoteApiSettings settings,
        out string listenerPrefix,
        out string displayAddress,
        out string error)
    {
        listenerPrefix = string.Empty;
        displayAddress = string.Empty;
        error = string.Empty;
        var host = settings.Host.Trim().Trim('[', ']');
        if (host.Length == 0 || host.Contains('/') || host.Contains('\\') || host.Contains("://", StringComparison.Ordinal))
        {
            error = "Enter a host name or IP address without a scheme or path.";
            return false;
        }

        if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            error = "The HTTP host is not a valid host name or IP address.";
            return false;
        }

        if (settings.Port is < 1 or > 65535)
        {
            error = "The HTTP port must be between 1 and 65535.";
            return false;
        }

        var displayHost = host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;
        var listenerHost = host is "0.0.0.0" or "::" ? "*" : displayHost;
        listenerPrefix = $"http://{listenerHost}:{settings.Port}/";
        displayAddress = $"http://{displayHost}:{settings.Port}/";
        return true;
    }

    public void Dispose()
    {
        lock (gate)
        {
            Stop();
        }
    }
}
