using System;

namespace LightMediaRenderer.Contracts;

/// <summary>A normalized piecewise reshaping curve for one Dolby Vision component.</summary>
public sealed class DolbyVisionReshapingCurve
{
	public const int MaximumPieces = 8;
	public const int MaximumPivots = MaximumPieces + 1;
	public const int PolynomialCoefficientCount = 3;
	public const int MaximumMmrOrder = 3;
	public const int MmrCoefficientCount = 7;

	public int PivotCount { get; set; }

	public float[] Pivots { get; } = new float[MaximumPivots];

	/// <summary>Zero for a polynomial piece, one for an MMR piece.</summary>
	public float[] Methods { get; } = new float[MaximumPieces];

	public float[] PolynomialCoefficients { get; } =
		new float[MaximumPieces * PolynomialCoefficientCount];

	public float[] MmrOrders { get; } = new float[MaximumPieces];

	public float[] MmrConstants { get; } = new float[MaximumPieces];

	public float[] MmrCoefficients { get; } =
		new float[MaximumPieces * MaximumMmrOrder * MmrCoefficientCount];

	public void Reset()
	{
		PivotCount = 0;
		Array.Clear(Pivots);
		Array.Clear(Methods);
		Array.Clear(PolynomialCoefficients);
		Array.Clear(MmrOrders);
		Array.Clear(MmrConstants);
		Array.Clear(MmrCoefficients);
	}
}

/// <summary>Parsed Dolby Vision metadata owned and reused by one decoded frame.</summary>
public sealed class DolbyVisionMetadata
{
	public const int ComponentCount = 3;
	public const int MatrixElementCount = 9;

	public bool IsPresent { get; set; }

	/// <summary>
	/// Whether the metadata is present and every value it carries is finite, so a renderer can apply
	/// it; a record holding a NaN or an infinity is left unapplied and the frame renders as plain PQ.
	/// </summary>
	public bool IsApplicable => IsPresent && HasFiniteValues();

	public int Profile { get; set; }

	public int Level { get; set; }

	public int CompatibilityId { get; set; }

	public int BaseLayerBitDepth { get; set; }

	public int EnhancementLayerBitDepth { get; set; }

	public bool BaseLayerFullRange { get; set; }

	/// <summary>Whether this picture needs its decoded enhancement-layer residual composed.</summary>
	public bool EnhancementLayerResidual { get; set; }

	/// <summary>Normalized LINEAR_DZ center for each enhancement-layer component.</summary>
	public float[] NlqOffset { get; } = new float[ComponentCount];

	/// <summary>Normalized LINEAR_DZ slope for each enhancement-layer component.</summary>
	public float[] NlqDeadzoneSlope { get; } = new float[ComponentCount];

	/// <summary>Normalized LINEAR_DZ threshold for each enhancement-layer component.</summary>
	public float[] NlqDeadzoneThreshold { get; } = new float[ComponentCount];

	public float SourceMinimumPq { get; set; }

	public float SourceMaximumPq { get; set; }

	public float SceneMaximumPq { get; set; }

	public float SceneAveragePq { get; set; }

	public double EffectivePeakLuminance { get; set; }

	public float[] NonlinearOffset { get; } = new float[ComponentCount];

	public float[] NonlinearMatrix { get; } = new float[MatrixElementCount];

	public float[] LinearMatrix { get; } = new float[MatrixElementCount];

	public DolbyVisionReshapingCurve[] Curves { get; } =
	[
		new DolbyVisionReshapingCurve(),
		new DolbyVisionReshapingCurve(),
		new DolbyVisionReshapingCurve(),
	];

	public void Reset()
	{
		IsPresent = false;
		Profile = 0;
		Level = 0;
		CompatibilityId = 0;
		BaseLayerBitDepth = 0;
		EnhancementLayerBitDepth = 0;
		BaseLayerFullRange = false;
		EnhancementLayerResidual = false;
		Array.Clear(NlqOffset);
		Array.Clear(NlqDeadzoneSlope);
		Array.Clear(NlqDeadzoneThreshold);
		SourceMinimumPq = 0;
		SourceMaximumPq = 0;
		SceneMaximumPq = 0;
		SceneAveragePq = 0;
		EffectivePeakLuminance = 0;
		Array.Clear(NonlinearOffset);
		Array.Clear(NonlinearMatrix);
		Array.Clear(LinearMatrix);
		foreach (DolbyVisionReshapingCurve curve in Curves)
			curve.Reset();
	}

	private bool HasFiniteValues()
	{
		if (!float.IsFinite(SourceMinimumPq) || !float.IsFinite(SourceMaximumPq) ||
			!float.IsFinite(SceneMaximumPq) || !float.IsFinite(SceneAveragePq) ||
			!double.IsFinite(EffectivePeakLuminance))
		{
			return false;
		}

		foreach (float[] values in (float[][])[NlqOffset, NlqDeadzoneSlope, NlqDeadzoneThreshold, NonlinearOffset, NonlinearMatrix, LinearMatrix])
		{
			if (!AllFinite(values))
				return false;
		}

		foreach (DolbyVisionReshapingCurve curve in Curves)
		{
			if (!AllFinite(curve.Pivots) || !AllFinite(curve.Methods) || !AllFinite(curve.PolynomialCoefficients) ||
				!AllFinite(curve.MmrOrders) || !AllFinite(curve.MmrConstants) || !AllFinite(curve.MmrCoefficients))
			{
				return false;
			}
		}

		return true;
	}

	private static bool AllFinite(float[] values)
	{
		foreach (float value in values)
		{
			if (!float.IsFinite(value))
				return false;
		}

		return true;
	}
}