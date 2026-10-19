namespace LightMediaRenderer.Contracts;

/// <summary>Receives the processing choices made by a render call.</summary>
public interface IVideoRenderDiagnostics
{
    void RecordScaling(VideoScalingQuality effective, string declined);

    void RecordColorProgram(bool drawnWithoutShader);

    void RecordPreScale(VideoPreScaleStatus? active);

    void RecordDynamicHdr(bool dolbyVisionApplied, VideoHdr10PlusStatus? hdr10Plus);

    void RecordDither(int outputBitDepth, bool dithered, bool panelPinned);
}

public interface IVideoToneMappingDiagnostics
{
    void RecordToneMapping(string algorithm);
}

public readonly record struct VideoPreScaleStatus(
    string Name,
    int InputWidth,
    int InputHeight,
    int OutputWidth,
    int OutputHeight,
    int PassCount);

public enum VideoHdr10PlusMapping
{
    MetadataOnly,
    SceneAdaptiveSpline,
    St2094_40,
}

public readonly record struct VideoHdr10PlusStatus(
    int ApplicationVersion,
    int WindowCount,
    double MetadataTargetNits,
    double ScenePeakNits,
    double SceneAverageNits,
    int AnchorCount,
    VideoHdr10PlusMapping Mapping);