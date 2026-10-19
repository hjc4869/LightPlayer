using System;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using LightStudio.FfmpegShim.Media;

namespace LightStudio.LightPlayer.Remote;

public static class RemoteStreamIdentity
{
    public static string ParametersKey(CodecParameters parameters) =>
        Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(parameters, RemoteStreamIdentityJsonContext.Default.CodecParameters)));

    public static string Fingerprint(params string[] values) =>
        Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(values, RemoteApiJsonContext.Default.StringArray)));
}

/// <summary>
/// Stored codec values only: derived ones add nothing to an identity, and some of them, such as the bit
/// depth, ask FFmpeg, which a browser page does not have.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    IgnoreReadOnlyProperties = true)]
[JsonSerializable(typeof(CodecParameters))]
internal sealed partial class RemoteStreamIdentityJsonContext : JsonSerializerContext;

public sealed class RemoteStreamNegotiationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}