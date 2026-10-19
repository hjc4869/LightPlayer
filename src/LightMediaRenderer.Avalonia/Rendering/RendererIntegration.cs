using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using LightMediaRenderer;
using LightMediaRenderer.Contracts;
using LightStudio.Logging;

namespace LightMediaRenderer.Avalonia.Rendering;

internal static class RendererIntegration
{
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255", Justification = "This application assembly installs renderer logging before its first use.")]
    internal static void Initialize() => RendererLog.Message += message =>
    {
        switch (message.Level)
        {
            case RendererLogLevel.Debug:
                AppLog.Debug(message.Category, message.Message);
                break;
            case RendererLogLevel.Information:
                AppLog.Information(message.Category, message.Message);
                break;
            case RendererLogLevel.Warning:
                AppLog.Warning(message.Category, message.Message, message.Exception);
                break;
            case RendererLogLevel.Error:
                AppLog.Error(message.Category, message.Message, message.Exception);
                break;
        }
    };
}