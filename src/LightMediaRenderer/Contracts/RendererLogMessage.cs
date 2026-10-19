using System;

namespace LightMediaRenderer.Contracts;

public enum RendererLogLevel
{
    Debug,
    Information,
    Warning,
    Error,
}

public readonly record struct RendererLogMessage(
    RendererLogLevel Level, string Category, string Message, Exception? Exception);