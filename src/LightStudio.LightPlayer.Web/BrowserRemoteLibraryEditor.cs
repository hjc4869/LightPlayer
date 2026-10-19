using System;
using System.Buffers;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Styling;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.ViewModels.Pages;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Web;

/// <summary>
/// Presents the remote-library editor as an HTML form instead of the Avalonia one, so the browser's
/// own password manager can fill it in and offer to remember what the user types. The form itself
/// lives in <c>wwwroot/remote-library-editor.js</c>; it is a twin of
/// <c>Views/Pages/RemoteLibraryEditorView.axaml</c> and both have to be changed together.
/// </summary>
/// <remarks>
/// The view model still does the saving, so this only moves the fields: values come back from the
/// page, get pushed into <see cref="RemoteLibraryEditorViewModel"/>, and its save command runs. A
/// save that fails re-shows the same form with the message, exactly as the XAML form does.
/// </remarks>
[SupportedOSPlatform("browser")]
internal static partial class BrowserRemoteLibraryEditor
{
    public static void Register() => PlatformServices.RemoteLibraryEditorPresenter = PresentAsync;

    private static async Task PresentAsync(RemoteLibraryEditorViewModel viewModel)
    {
        var saved = false;
        void OnRequestClose() => saved = true;

        viewModel.RequestClose += OnRequestClose;
        try
        {
            while (!saved)
            {
                var state = Describe(viewModel);
                var submission = await BrowserMainThread.InvokeAsync(() => ShowAsync(state));
                if (submission.Length == 0)
                {
                    viewModel.CancelCommand.Execute(null);
                    return;
                }

                var offline = Apply(viewModel, submission);
                await (offline ? viewModel.SaveOfflineCommand : viewModel.SaveCommand).ExecuteAsync(null);
            }
        }
        catch (Exception exception)
        {
            // Left to the command, this would be rethrown on the dispatcher and stop the whole app.
            AppLog.Warning("Web.RemoteLibraryEditor", "The remote library form was abandoned.", exception);
        }
        finally
        {
            viewModel.RequestClose -= OnRequestClose;
            await BrowserMainThread.InvokeAsync(CloseAsync);
        }
    }

    /// <summary>Everything the page needs to draw the form; the keys match the ones it reads.</summary>
    private static string Describe(RemoteLibraryEditorViewModel viewModel)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("dark", Application.Current?.ActualThemeVariant != ThemeVariant.Light);
            writer.WriteString("header", viewModel.Header);
            writer.WriteString("nameLabel", viewModel.NameText);
            writer.WriteString("namePlaceholder", viewModel.NamePlaceholderText);
            writer.WriteString("providerLabel", viewModel.ProviderText);
            writer.WriteString("lightPlayerLabel", viewModel.ProviderOptions[0]);
            writer.WriteString("nextcloudMusicLabel", viewModel.ProviderOptions[1]);
            writer.WriteString("webDavLabel", viewModel.ProviderOptions[2]);
            writer.WriteString("addressLabel", viewModel.AddressText);
            writer.WriteString("addressPlaceholder", "https://server.example");
            writer.WriteString("loadMusicLabel", viewModel.LoadMusicText);
            writer.WriteString("loadVideoLabel", viewModel.LoadVideoText);
            writer.WriteString("refreshAtStartupLabel", viewModel.RefreshAtStartupText);
            writer.WriteString("webProxyLabel", viewModel.WebProxyText);
            writer.WriteString("webProxyHint", viewModel.WebProxyHintText);
            writer.WriteString("userNameLabel", viewModel.UserNameText);
            writer.WriteString("passwordLabel", viewModel.PasswordText);
            writer.WriteString("rememberPasswordLabel", viewModel.RememberPasswordText);
            writer.WriteString("apiKeyLabel", viewModel.ApiKeyText);
            writer.WriteString("apiKeyHint", viewModel.ApiKeyHintText);
            writer.WriteString("openNextcloudMusicLabel", viewModel.OpenNextcloudMusicText);
            writer.WriteString("saveLabel", Strings.Resources.Common_Save);
            writer.WriteString("saveOfflineLabel", viewModel.SaveOfflineText);
            writer.WriteString("cancelLabel", Strings.Resources.Common_Cancel);
            writer.WriteString("name", viewModel.Name);
            writer.WriteString("address", viewModel.Address);
            writer.WriteString("userName", viewModel.UserName);
            writer.WriteString("password", viewModel.Password);
            writer.WriteString("apiKey", viewModel.ApiKey);
            writer.WriteNumber("selectedProviderIndex", viewModel.SelectedProviderIndex);
            writer.WriteBoolean("loadMusic", viewModel.LoadMusic);
            writer.WriteBoolean("loadVideo", viewModel.LoadVideo);
            writer.WriteBoolean("refreshAtStartup", viewModel.RefreshAtStartup);
            writer.WriteBoolean("useWebProxy", viewModel.UseWebProxy);
            writer.WriteBoolean("rememberPassword", viewModel.RememberPassword);
            writer.WriteBoolean("canSaveOffline", viewModel.CanSaveOffline);
            writer.WriteString("error", viewModel.ErrorMessage);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Copies the submitted form back into the view model; returns the offline request.</summary>
    private static bool Apply(RemoteLibraryEditorViewModel viewModel, string submission)
    {
        using var document = JsonDocument.Parse(submission);
        var root = document.RootElement;
        viewModel.Name = Text(root, "name");
        viewModel.Address = Text(root, "address");
        viewModel.SelectedProviderIndex = Number(root, "selectedProviderIndex");
        viewModel.UserName = Text(root, "userName");
        viewModel.Password = Text(root, "password");
        viewModel.ApiKey = Text(root, "apiKey");
        viewModel.LoadMusic = Flag(root, "loadMusic");
        viewModel.LoadVideo = Flag(root, "loadVideo");
        viewModel.RefreshAtStartup = Flag(root, "refreshAtStartup");
        viewModel.UseWebProxy = Flag(root, "useWebProxy");
        viewModel.RememberPassword = Flag(root, "rememberPassword");
        return Flag(root, "offline");
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool Flag(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static int Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;

    [JSImport("show", "RemoteLibraryEditor")]
    private static partial Task<string> ShowAsync(string state);

    [JSImport("close", "RemoteLibraryEditor")]
    private static partial Task CloseAsync();
}
