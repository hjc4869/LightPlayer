using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Services;

namespace LightStudio.RemoteServices;

internal sealed record NextcloudMusicAuthorization(
    string ServerAddress,
    string Username,
    string ApiKey);

internal static class NextcloudMusicLoginFlow
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FlowLifetime = TimeSpan.FromMinutes(20);

    public static async Task<NextcloudMusicAuthorization> AuthenticateAsync(
        string serverAddress,
        Func<Uri, CancellationToken, Task> openBrowser,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        if (!OperatingSystem.IsBrowser())
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("LightPlayer/1.0");
        }
        var requestedServer = ServerRoot(serverAddress);
        var flow = await BeginAsync(client, requestedServer, cancellationToken).ConfigureAwait(false);
        await openBrowser(flow.LoginUri, cancellationToken).ConfigureAwait(false);
        var credentials = await PollAsync(client, flow, cancellationToken).ConfigureAwait(false);

        MusicKey? musicKey = null;
        var musicKeyDescription = $"Light Media Center {Guid.NewGuid():N}";
        try
        {
            var requestToken = await EstablishNextcloudSessionAsync(
                client,
                credentials,
                cancellationToken).ConfigureAwait(false);
            musicKey = await CreateMusicKeyAsync(
                client,
                credentials,
                musicKeyDescription,
                requestToken,
                cancellationToken).ConfigureAwait(false);
            using var musicClient = new NextcloudMusicClient(credentials.ServerAddress, musicKey.Value);
            var username = await musicClient.GetUserNameAsync(cancellationToken).ConfigureAwait(false);
            await RevokeAppPasswordAsync(client, credentials, cancellationToken).ConfigureAwait(false);

            return new NextcloudMusicAuthorization(credentials.ServerAddress, username, musicKey.Value);
        }
        catch (Exception failure)
        {
            var cleanupFailures = new List<Exception>();
            if (musicKey is not null)
            {
                try
                {
                    await DeleteMusicKeyAsync(client, credentials, musicKey.Id, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception cleanupFailure)
                {
                    cleanupFailures.Add(cleanupFailure);
                }
            }
            else
            {
                try
                {
                    await DeleteMusicKeysByDescriptionAsync(
                        client,
                        credentials,
                        musicKeyDescription,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception cleanupFailure)
                {
                    cleanupFailures.Add(cleanupFailure);
                }
            }

            try
            {
                await RevokeAppPasswordAsync(client, credentials, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception revocationFailure)
            {
                cleanupFailures.Add(revocationFailure);
            }
            if (cleanupFailures.Count > 0)
            {
                cleanupFailures.Insert(0, failure);
                throw new AggregateException("Nextcloud authentication and credential cleanup failed.", cleanupFailures);
            }
            throw;
        }
    }

    private static async Task<LoginFlow> BeginAsync(
        HttpClient client,
        Uri server,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostAsync(
            AppendPath(server, "index.php/login/v2"),
            content: null,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var poll = document.RootElement.GetProperty("poll");
        return new LoginFlow(
            new Uri(RequiredString(document.RootElement, "login"), UriKind.Absolute),
            new Uri(RequiredString(poll, "endpoint"), UriKind.Absolute),
            RequiredString(poll, "token"));
    }

    private static async Task<LoginCredentials> PollAsync(
        HttpClient client,
        LoginFlow flow,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + FlowLifetime;
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = flow.PollToken,
            });
            using var response = await client
                .PostAsync(flow.PollUri, content, cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var server = ServerRoot(RequiredString(document.RootElement, "server"));
            if (server.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidDataException("Nextcloud Login Flow returned a non-HTTPS server address.");
            }
            return new LoginCredentials(
                server.ToString().TrimEnd('/'),
                RequiredString(document.RootElement, "loginName"),
                RequiredString(document.RootElement, "appPassword"));
        }

        throw new TimeoutException("Nextcloud browser authentication was not completed within 20 minutes.");
    }

    private static async Task<string> EstablishNextcloudSessionAsync(
        HttpClient client,
        LoginCredentials credentials,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            AppendPath(new Uri(credentials.ServerAddress + "/"), "index.php/csrftoken"));
        SetNextcloudAuthentication(request, credentials);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return RequiredString(document.RootElement, "token");
    }

    private static async Task<MusicKey> CreateMusicKeyAsync(
        HttpClient client,
        LoginCredentials credentials,
        string description,
        string requestToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            AppendPath(new Uri(credentials.ServerAddress + "/"), "index.php/apps/music/api/settings/user/keys"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["length"] = "40",
                ["description"] = description,
            }),
        };
        SetNextcloudAuthentication(request, credentials);
        request.Headers.TryAddWithoutValidation("requesttoken", requestToken);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new MusicKey(
            document.RootElement.GetProperty("id").GetInt32(),
            RequiredString(document.RootElement, "password"));
    }

    private static async Task RevokeAppPasswordAsync(
        HttpClient client,
        LoginCredentials credentials,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            AppendPath(new Uri(credentials.ServerAddress + "/"), "ocs/v2.php/core/apppassword"));
        SetNextcloudAuthentication(request, credentials);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private static async Task DeleteMusicKeyAsync(
        HttpClient client,
        LoginCredentials credentials,
        int keyId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            AppendPath(
                new Uri(credentials.ServerAddress + "/"),
                $"index.php/apps/music/api/settings/user/keys/{keyId}"));
        SetNextcloudAuthentication(request, credentials);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private static async Task DeleteMusicKeysByDescriptionAsync(
        HttpClient client,
        LoginCredentials credentials,
        string description,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            AppendPath(new Uri(credentials.ServerAddress + "/"), "index.php/apps/music/api/settings/user/keys"));
        SetNextcloudAuthentication(request, credentials);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Nextcloud Music returned an invalid API-key list.");
        }

        var keyIds = new List<int>();
        foreach (var key in document.RootElement.EnumerateArray())
        {
            if (key.TryGetProperty("description", out var keyDescription)
                && string.Equals(keyDescription.GetString(), description, StringComparison.Ordinal)
                && key.TryGetProperty("id", out var keyId)
                && keyId.TryGetInt32(out var id))
            {
                keyIds.Add(id);
            }
        }

        foreach (var keyId in keyIds)
        {
            await DeleteMusicKeyAsync(client, credentials, keyId, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void SetNextcloudAuthentication(HttpRequestMessage request, LoginCredentials credentials)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credentials.LoginName}:{credentials.AppPassword}")));
        request.Headers.TryAddWithoutValidation("OCS-APIRequest", "true");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static Uri AppendPath(Uri server, string path) =>
        new(server.ToString().TrimEnd('/') + "/" + path.TrimStart('/'), UriKind.Absolute);

    private static Uri ServerRoot(string address)
    {
        var builder = new UriBuilder(new Uri(address.Trim().TrimEnd('/') + "/", UriKind.Absolute))
        {
            Query = string.Empty,
            Fragment = string.Empty,
        };
        var path = builder.Path.TrimEnd('/');
        if (path.EndsWith("/index.php", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^"/index.php".Length];
        }
        builder.Path = path + "/";
        return builder.Uri;
    }

    private static string RequiredString(JsonElement value, string propertyName) =>
        value.TryGetProperty(propertyName, out var property) && property.GetString() is { Length: > 0 } text
            ? text
            : throw new InvalidDataException($"Nextcloud response omitted '{propertyName}'.");

    private sealed record LoginFlow(Uri LoginUri, Uri PollUri, string PollToken);
    private sealed record LoginCredentials(string ServerAddress, string LoginName, string AppPassword);
    private sealed record MusicKey(int Id, string Value);
}