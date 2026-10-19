using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace LightStudio.Inference;

internal sealed record LiteRtModelFile(string Name, string RepositoryId, string Revision, long Size, string Sha256)
{
    internal Uri DownloadUri => new($"https://huggingface.co/{RepositoryId}/resolve/{Revision}/{Name}");

    internal async Task<bool> VerifyAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        if (stream.Length != Size)
            return false;
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).Equals(Sha256, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class LiteRtEmbeddingModels
{
    internal const string Id = "litert-community/SigLIP2-base-patch16-224";
    internal const string Name = "SigLIP2 ViT-B/16 (224)";
    internal const string ImageFileName = "siglip2_base_224_fp16.tflite";
    internal const string TextFileName = "siglip2_text.tflite";
    internal const int ImageSize = 224;
    internal const int SequenceLength = 64;
    internal const int EmbeddingDimensions = 768;

    internal static IReadOnlyList<LiteRtModelFile> Files { get; } =
    [
        new(ImageFileName, Id, "509b5cbcf1a849f37696be08f8297c6cd3050bf4", 185437744,
            "a30ebb7b3ee15eaa68a18f9ab6a2ed740c15c343d25d898dc482317473320854"),
        new(TextFileName, "ippocode/siglip2-text-tflite", "75daa5ccac123e8b08dac13b9c7e9fbdef8e82ba", 287126880,
            "10ee262d73d6cbd6170c4eab5628fada2c2865f702fe2d7f3947695a7b5e84ad"),
        new("tokenizer.model", "google/siglip2-base-patch16-224", "75de2d55ec2d0b4efc50b3e9ad70dba96a7b2fa2", 4241003,
            "61a7b147390c64585d6c3543dd6fc636906c9af3865a5548f27f31aee1d4c8e2"),
        new("tokenizer_config.json", "google/siglip2-base-patch16-224", "75de2d55ec2d0b4efc50b3e9ad70dba96a7b2fa2", 47164,
            "14afe629fe4959b9e0d51e1852b8d9f7ad074f90a1a7125a4fcdd17f06e78fc8"),
    ];
}