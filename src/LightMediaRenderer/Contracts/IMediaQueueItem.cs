namespace LightMediaRenderer.Contracts;

/// <summary>A media payload associated with one contiguous playback generation.</summary>
public interface IMediaQueueItem
{
    int Serial { get; set; }

    int SizeInBytes { get; }

    bool IsEndOfStream { get; }
}