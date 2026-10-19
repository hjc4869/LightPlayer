using System;
using System.Threading.Tasks;

using LightStudio.Logging;

using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Sink for failures raised by command handlers and background work that have no local surface to
/// report to. In client mode every library, playlist and lyric call goes over the network, so any
/// of them can fail when the server goes away; without this the exception escapes an
/// <c>async void</c> handler or an <see cref="CommunityToolkit.Mvvm.Input.AsyncRelayCommand"/> and
/// terminates the process.
/// </summary>
public static class AppErrorReporter
{
    /// <summary>
    /// Receives a short title and the failure detail. The shell registers itself here to show them
    /// in its warning banner; assigning replaces the previous shell rather than adding to it.
    /// </summary>
    public static Action<string, string>? Handler { get; set; }

    /// <summary>Reports <paramref name="exception"/> under <paramref name="title"/>.</summary>
    public static void Report(string title, Exception exception)
    {
        string message = LogAndGetMessage("Application", title, exception);
        Handler?.Invoke(title, message);
    }

    /// <summary>Logs a complete exception and returns its concise user-facing message.</summary>
    public static string LogAndGetMessage(string category, string context, Exception exception)
    {
        AppLog.Error(category, context, exception);
        return exception.GetBaseException().Message;
    }

    /// <summary>
    /// Awaits <paramref name="work"/> and reports any failure instead of letting it escape.
    /// Cancellation is expected (the user navigated away) and stays silent.
    /// </summary>
    public static async Task GuardAsync(Func<Task> work, string? title = null)
    {
        try
        {
            await work().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Report(title ?? Strings.Resources.Shell_WarnActionFailed, exception);
        }
    }
}
