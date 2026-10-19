using System;
using System.Diagnostics;
using LightMediaRenderer.Contracts;

namespace LightMediaRenderer;

/// <summary>Reports shader compilation and graphics capability diagnostics to the host.</summary>
public static class RendererLog
{
    public static event Action<RendererLogMessage>? Message;

    internal static void Debug(string category, string message) =>
        Write(RendererLogLevel.Debug, category, message, null);

    internal static void Information(string category, string message) =>
        Write(RendererLogLevel.Information, category, message, null);

    internal static void Warning(string category, string message, Exception? exception = null) =>
        Write(RendererLogLevel.Warning, category, message, exception);

    internal static void Error(string category, string message, Exception? exception = null) =>
        Write(RendererLogLevel.Error, category, message, exception);

    private static void Write(RendererLogLevel level, string category, string message, Exception? exception)
    {
        if (Message is { } handler)
            handler(new RendererLogMessage(level, category, message, exception));
        else
            Trace.WriteLine($"[{category}] {message}{(exception is null ? string.Empty : Environment.NewLine + exception)}");
    }
}