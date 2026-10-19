using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace LightStudio.Inference;

internal static class OnnxAudioModels
{
    internal const string Repository = "Blane187/all_public_uvr_models";
    internal static IReadOnlyList<OnnxModelPackage> All { get; } = new[]
    {
        "Kim_Inst.onnx", "Kim_Vocal_1.onnx", "Kim_Vocal_2.onnx", "Reverb_HQ_By_FoxJoy.onnx",
        "UVR-MDX-NET-Inst_1.onnx", "UVR-MDX-NET-Inst_2.onnx", "UVR-MDX-NET-Inst_3.onnx",
        "UVR-MDX-NET-Inst_HQ_1.onnx", "UVR-MDX-NET-Inst_HQ_2.onnx", "UVR-MDX-NET-Inst_HQ_3.onnx",
        "UVR-MDX-NET-Inst_HQ_4.onnx", "UVR-MDX-NET-Inst_Main.onnx", "UVR-MDX-NET-Voc_FT.onnx",
        "UVR-MDX-NET_Crowd_HQ_1.onnx", "UVR_MDXNET_1_9703.onnx", "UVR_MDXNET_2_9682.onnx",
        "UVR_MDXNET_3_9662.onnx", "UVR_MDXNET_9482.onnx", "UVR_MDXNET_KARA.onnx",
        "UVR_MDXNET_KARA_2.onnx", "UVR_MDXNET_Main.onnx", "kuielab_a_bass.onnx",
        "kuielab_a_drums.onnx", "kuielab_a_other.onnx", "kuielab_a_vocals.onnx",
        "kuielab_b_bass.onnx", "kuielab_b_drums.onnx", "kuielab_b_other.onnx", "kuielab_b_vocals.onnx",
    }.Select(name => new OnnxModelPackage(name, Repository, name, [])).ToArray();

    private static readonly IReadOnlyDictionary<string, MdxProfile> profiles = new Dictionary<string, MdxProfile>(StringComparer.OrdinalIgnoreCase)
    {
        ["0ddfc0eb5792638ad5dc27850236c246"] = new(6144, 2048, 256, 1.035f),
        ["26d308f91f3423a67dc69a6d12a8793d"] = new(8192, 2048, 512, 1.035f),
        ["2cdd429caac38f0194b133884160f2c6"] = new(7680, 3072, 256, 1.045f),
        ["2f5501189a2f6db6349916fabe8c90de"] = new(6144, 2048, 256, 1.035f),
        ["398580b6d5d973af3120df54cee6759d"] = new(7680, 3072, 256, 1.75f),
        ["488b3e6f8bd3717d9d7c428476be2d75"] = new(7680, 3072, 256, 1.035f),
        ["4910e7827f335048bdac11fa967772f9"] = new(4096, 2048, 128, 1.035f),
        ["53c4baf4d12c3e6c3831bb8f5b532b93"] = new(7680, 3072, 256, 1.043f),
        ["5d343409ef0df48c7d78cce9f0106781"] = new(7680, 3072, 256, 1.075f),
        ["5f6483271e1efb9bfb59e4a3e6d4d098"] = new(6144, 2048, 512, 1.035f),
        ["65ab5919372a128e4167f5e01a8fda85"] = new(8192, 2048, 256, 1.035f),
        ["6703e39f36f18aa7855ee1047765621d"] = new(16384, 2048, 512, 1.035f),
        ["6b31de20e84392859a3d09d43f089515"] = new(6144, 2048, 256, 1.035f),
        ["867595e9de46f6ab699008295df62798"] = new(7680, 3072, 256, 1.03f),
        ["a3cd63058945e777505c01d2507daf37"] = new(6144, 2048, 256, 1.03f),
        ["b33d9b3950b6cbf5fe90a32608924700"] = new(7680, 3072, 256, 1.03f),
        ["c3b29bdce8c4fa17ec609e16220330ab"] = new(16384, 2048, 256, 1.035f),
        ["ceed671467c1f64ebdfac8a2490d0d52"] = new(7680, 3072, 256, 1.035f),
        ["d2a1376f310e4f7fa37fb9b5774eb701"] = new(7680, 3072, 256, 1.035f),
        ["d7bff498db9324db933d913388cba6be"] = new(6144, 2048, 256, 1.035f),
        ["d94058f8c7f1fae4164868ae8ae66b20"] = new(6144, 2048, 256, 1.035f),
        ["dc41ede5961d50f277eb846db17f5319"] = new(4096, 2048, 512, 1.035f),
        ["e5572e58abf111f80d8241d2e44e7fa4"] = new(7680, 3072, 256, 1.028f),
        ["e7324c873b1f615c35c1967f912db92a"] = new(7680, 3072, 256, 1.03f),
        ["1c56ec0224f1d559c42fd6fd2a67b154"] = new(5120, 2048, 256, 1.025f),
        ["f2df6d6863d8f435436d8b561594ff49"] = new(7680, 3072, 256, 1.035f),
        ["b06327a00d5e5fbc7d96e1781bbdb596"] = new(6144, 3072, 256, 1.035f),
        ["94ff780b977d3ca07c7a343dab2e25dd"] = new(6144, 3072, 256, 1.039f),
        ["73492b58195c3b52d34590d5474452f6"] = new(7680, 3072, 256, 1.043f),
        ["970b3f9492014d18fefeedfe4773cb42"] = new(7680, 3072, 256, 1.009f),
        ["1d64a6d2c30f709b8c9b4ce1366d96ee"] = new(5120, 2048, 256, 1.065f),
        ["203f2a3955221b64df85a41af87cf8f0"] = new(6144, 3072, 256, 1.035f),
        ["291c2049608edb52648b96e27eb80e95"] = new(6144, 3072, 256, 1.035f),
        ["ead8d05dab12ec571d67549b3aab03fc"] = new(6144, 3072, 256, 1.035f),
        ["cc63408db3d80b4d85b0287d1d7c9632"] = new(6144, 3072, 256, 1.033f),
        ["cd5b2989ad863f116c855db1dfe24e39"] = new(6144, 3072, 512, 1.035f),
        ["55657dd70583b0fedfba5f67df11d711"] = new(6144, 3072, 256, 1.022f),
        ["b6bccda408a436db8500083ef3491e8b"] = new(7680, 3072, 256, 1.02f),
        ["8a88db95c7fb5dbe6a095ff2ffb428b1"] = new(5120, 2048, 256, 1.026f),
        ["b78da4afc6512f98e4756f5977f5c6b9"] = new(7680, 3072, 256, 1.021f),
        ["77d07b2667ddf05b9e3175941b4454a0"] = new(7680, 3072, 256, 1.021f),
        ["0f2a6bc5b49d87d64728ee40e23bceb1"] = new(5120, 2560, 256, 1.019f),
        ["cb790d0c913647ced70fc6b38f5bea1a"] = new(5120, 2560, 256, 1.01f),
        ["b02be2d198d4968a121030cf8950b492"] = new(5120, 2560, 256, 1.02f),
    };

    internal static MdxProfile GetProfile(string path, int frequencies)
    {
        using var file = File.OpenRead(path);
        file.Position = Math.Max(0, file.Length - 10000 * 1024);
        var fingerprint = Convert.ToHexString(MD5.HashData(file));
        return GetProfileByFingerprint(fingerprint, frequencies);
    }

    internal static MdxProfile GetProfileByFingerprint(string fingerprint, int frequencies) =>
        profiles.TryGetValue(fingerprint, out var profile) && profile.Frequencies == frequencies ? profile
            : new MdxProfile(Math.Max(6144, (frequencies - 1) * 2), frequencies, 256, 1f);
}

internal sealed record MdxProfile(int FftSize, int Frequencies, int Frames, float Gain);