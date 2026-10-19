using System;
using System.Collections.Generic;
using System.Globalization;
using LightStudio.LightPlayer.Services;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace LightStudio.LightPlayer.Server.Tui;

/// <summary>Listener, credentials and the paths the deployment keeps its data in.</summary>
internal sealed class ServerSettingsTab : View, IConfigurationTab
{
    private readonly TextField host;
    private readonly TextField port;
    private readonly TextField allowedOrigins;
    private readonly CheckBox proxyOnly;
    private readonly TextField username;
    private readonly TextField password;
    private readonly TextField dataPath;
    private readonly TextField cachePath;
    private readonly TextField staticFilesPath;
    private string passwordHash;

    public ServerSettingsTab(ServerConfiguration config)
    {
        Title = "S_erver";
        Width = Dim.Fill();
        Height = Dim.Fill();
        passwordHash = config.PasswordHash.Length > 0
            ? config.PasswordHash
            : config.Password.Length > 0
                ? RemoteApiCredentials.HashPassword(config.Password)
                : string.Empty;

        host = TuiLayout.Field(this, 0, "Listen address", config.Host);
        port = TuiLayout.Field(this, 1, "Port", config.Port.ToString(CultureInfo.InvariantCulture));
        allowedOrigins = TuiLayout.Field(this, 2, "Allowed origins", string.Join(", ", config.AllowedOrigins));
        TuiLayout.Note(this, 3, "  Sites allowed to call this API from a browser. Empty allows only clients this server hosts.");
        proxyOnly = TuiLayout.Check(this, 4, "Serve the browser client only and relay the API to another server", config.ProxyOnly);

        username = TuiLayout.Field(this, 6, "User name", config.Username);
        password = TuiLayout.Field(this, 7, "New password", string.Empty, secret: true);
        TuiLayout.Note(
            this,
            8,
            passwordHash.Length > 0
                ? "  A password is set. Leave this empty to keep it; only the hash is written to the file."
                : "  No password is set yet. Authentication is required unless the relay mode above is on.");

        dataPath = TuiLayout.Field(this, 10, "Data folder", config.DataPath);
        cachePath = TuiLayout.Field(this, 11, "Cache folder", config.CachePath);
        staticFilesPath = TuiLayout.Field(this, 12, "Browser client folder", config.StaticFilesPath);
        TuiLayout.Note(this, 13, "  Path changes take effect the next time the server starts.");
    }

    public string? Apply(ServerConfiguration config)
    {
        if (!int.TryParse(port.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int portValue) ||
            portValue is < 1 or > 65535)
        {
            return "The port has to be a number between 1 and 65535.";
        }

        string user = username.Text?.Trim() ?? string.Empty;
        string newPassword = password.Text ?? string.Empty;
        if (newPassword.Length > 0)
        {
            passwordHash = RemoteApiCredentials.HashPassword(newPassword);
            password.Text = string.Empty;
        }

        bool relayOnly = proxyOnly.IsChecked();
        if (!relayOnly && (user.Length == 0 || passwordHash.Length == 0))
        {
            return "A user name and a password are required unless the server only relays to another one.";
        }

        config.Host = host.Text?.Trim() is { Length: > 0 } hostValue ? hostValue : "0.0.0.0";
        config.Port = portValue;
        config.AllowedOrigins = Split(allowedOrigins.Text);
        config.ProxyOnly = relayOnly;
        config.Username = user;
        config.Password = string.Empty;
        config.PasswordHash = passwordHash;
        config.DataPath = dataPath.Text?.Trim() ?? string.Empty;
        config.CachePath = cachePath.Text?.Trim() ?? string.Empty;
        config.StaticFilesPath = staticFilesPath.Text?.Trim() ?? string.Empty;
        return null;
    }

    private static IReadOnlyList<string> Split(string? value) =>
        value?.Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
}
