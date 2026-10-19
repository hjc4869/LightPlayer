using System;

namespace LightMediaRenderer.Contracts;

/// <summary>One processing window from SMPTE ST 2094-40 dynamic metadata.</summary>
public sealed class Hdr10PlusProcessingWindow
{
	public const int MaximumAnchors = 15;
	public const int MaximumPercentiles = 15;

	public float[] SceneMaximumLuminance { get; } = new float[3];

	public float SceneAverageLuminance { get; set; }

	public int PercentileCount { get; set; }

	public byte[] Percentages { get; } = new byte[MaximumPercentiles];

	public float[] PercentileLuminance { get; } = new float[MaximumPercentiles];

	public bool ToneMapping { get; set; }

	public float KneeX { get; set; }

	public float KneeY { get; set; }

	public int AnchorCount { get; set; }

	public float[] Anchors { get; } = new float[MaximumAnchors];

	public void Reset()
	{
		Array.Clear(SceneMaximumLuminance);
		SceneAverageLuminance = 0;
		PercentileCount = 0;
		Array.Clear(Percentages);
		Array.Clear(PercentileLuminance);
		ToneMapping = false;
		KneeX = 0;
		KneeY = 0;
		AnchorCount = 0;
		Array.Clear(Anchors);
	}
}

/// <summary>Parsed HDR10+ metadata owned and reused by one decoded frame.</summary>
public sealed class Hdr10PlusMetadata
{
	public const int MaximumWindows = 3;

	public bool IsPresent { get; set; }

	/// <summary>ST 2094-40 application version; versions zero and one are supported.</summary>
	public int ApplicationVersion { get; set; }

	public int WindowCount { get; set; }

	public float TargetedSystemDisplayMaximumLuminance { get; set; }

	/// <summary>Brightest MaxSCL value in the first window, including the ST 2094-40 fallback.</summary>
	public float EffectivePeakLuminance { get; set; }

	public float SceneAverageLuminance { get; set; }

	public Hdr10PlusProcessingWindow[] Windows { get; } =
	[
		new Hdr10PlusProcessingWindow(),
		new Hdr10PlusProcessingWindow(),
		new Hdr10PlusProcessingWindow(),
	];

	public void Reset()
	{
		IsPresent = false;
		ApplicationVersion = 0;
		WindowCount = 0;
		TargetedSystemDisplayMaximumLuminance = 0;
		EffectivePeakLuminance = 0;
		SceneAverageLuminance = 0;
		foreach (Hdr10PlusProcessingWindow window in Windows)
			window.Reset();
	}
}