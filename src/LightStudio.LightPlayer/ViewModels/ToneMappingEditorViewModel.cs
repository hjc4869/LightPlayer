using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightMediaRenderer.Contracts;
using LightStudio.LightPlayer.ViewModels.Pages;
using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.ViewModels;

internal enum ToneParameterLock
{
    None,
    Headroom,
    Luminance,
    ReferenceWhite,
}

/// <summary>One adjustable tone mapping option, optionally following the display's value.</summary>
public sealed partial class ToneParameterViewModel : ViewModelBase
{
    private readonly ToneMappingEditorViewModel owner;
    private readonly string labelKey;
    private readonly string format;
    private double shownValue;

    [ObservableProperty]
    private double sliderValue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LockToolTip))]
    private bool isLocked;

    [ObservableProperty]
    private bool isLockable;

    internal ToneParameterViewModel(
        ToneMappingEditorViewModel owner,
        string labelKey,
        double minimum,
        double maximum,
        double step,
        string format,
        Func<VideoSdrToneMappingOptions, double> read,
        Func<VideoSdrToneMappingOptions, double, VideoSdrToneMappingOptions> write,
        ToneParameterLock lockKind = ToneParameterLock.None)
    {
        this.owner = owner;
        this.labelKey = labelKey;
        this.format = format;
        Minimum = minimum;
        Maximum = maximum;
        Step = step;
        Read = read;
        Write = write;
        LockKind = lockKind;
        ToggleLockCommand = new RelayCommand(() => owner.ToggleLock(this));
    }

    public string Label => Localized(labelKey);

    public double Minimum { get; }

    public double Maximum { get; }

    public double Step { get; }

    public double LargeChange => Step * 5;

    public string ValueText => shownValue.ToString(format, CultureInfo.CurrentCulture);

    public string LockToolTip => Localized(IsLocked
        ? nameof(Strings.Resources.ToneMapping_Locked)
        : nameof(Strings.Resources.ToneMapping_Unlocked));

    public IRelayCommand ToggleLockCommand { get; }

    internal ToneParameterLock LockKind { get; }

    internal Func<VideoSdrToneMappingOptions, double> Read { get; }

    internal Func<VideoSdrToneMappingOptions, double, VideoSdrToneMappingOptions> Write { get; }

    internal double Snap(double value) =>
        Math.Round(Math.Clamp(Math.Round((value - Minimum) / Step) * Step + Minimum, Minimum, Maximum), 6);

    internal void Load(double shown, bool locked, bool lockable)
    {
        shownValue = shown;
        IsLocked = locked;
        IsLockable = lockable;
        SliderValue = Math.Clamp(shown, Minimum, Maximum);
        OnPropertyChanged(nameof(ValueText));
    }

    partial void OnSliderValueChanged(double value) => owner.OnParameterChanged(this, value);
}

/// <summary>
/// Edits a tone curve and its parameters. Headroom, target luminance, and reference white can follow
/// what the display reports, which leaves the corresponding option unset.
/// </summary>
public sealed partial class ToneMappingEditorViewModel : ViewModelBase
{
    private readonly bool fixedTarget;
    private VideoSdrToneMappingOptions? options;
    private VideoSdrToneMappingOptions automaticOptions = VideoSdrToneMappingOptions.Default with
    {
        Algorithm = VideoSdrToneMappingAlgorithm.Spline,
    };
    private VideoDisplayToneTarget displayTarget = new(1, VideoOutputColorSpaceExtensions.HdrReferenceWhiteNits);
    private VideoSdrToneMappingAlgorithm? parameterAlgorithm;
    private bool hdrTarget;
    private bool loading;

    [ObservableProperty]
    private VideoSettingsOption? selectedCurve;

    /// <param name="fixedTarget">
    /// Whether the caller sets the target instead of the display: headroom is not offered and no
    /// option follows the display.
    /// </param>
    public ToneMappingEditorViewModel(bool fixedTarget = false)
    {
        this.fixedTarget = fixedTarget;
        CurveOptions =
        [
            new(nameof(VideoSdrToneMappingAlgorithm.Auto), "Automatic", nameof(Strings.Resources.Photo_ToneAutomatic)),
            new(nameof(VideoSdrToneMappingAlgorithm.Spline), "PQ spline", nameof(Strings.Resources.Photo_ToneSpline)),
            new(nameof(VideoSdrToneMappingAlgorithm.Bt2390), "BT.2390"),
            new(nameof(VideoSdrToneMappingAlgorithm.Bt2446A), "BT.2446A"),
            new(nameof(VideoSdrToneMappingAlgorithm.Clip), "Clip", nameof(Strings.Resources.Photo_ToneClip)),
            new(nameof(VideoSdrToneMappingAlgorithm.Mobius), "Mobius", nameof(Strings.Resources.Photo_ToneMobius)),
            new(nameof(VideoSdrToneMappingAlgorithm.Reinhard), "Reinhard"),
            new(nameof(VideoSdrToneMappingAlgorithm.Hable), "Filmic (Hable)", nameof(Strings.Resources.Photo_ToneHable)),
            new(nameof(VideoSdrToneMappingAlgorithm.Gamma), "Gamma", nameof(Strings.Resources.Photo_ToneGamma)),
            new(nameof(VideoSdrToneMappingAlgorithm.Linear), "Linear", nameof(Strings.Resources.Photo_ToneLinear)),
        ];
        Load();
    }

    /// <summary>Raised with the new options after each change the user makes.</summary>
    public event Action<VideoSdrToneMappingOptions>? Edited;

    public IReadOnlyList<VideoSettingsOption> CurveOptions { get; }

    /// <summary>What the selected curve preserves and what it trades away.</summary>
    public string CurveDescription => Localized(Current.Algorithm switch
    {
        VideoSdrToneMappingAlgorithm.Spline => nameof(Strings.Resources.Photo_ToneSplineDescription),
        VideoSdrToneMappingAlgorithm.Bt2390 => nameof(Strings.Resources.Photo_ToneBt2390Description),
        VideoSdrToneMappingAlgorithm.Bt2446A => nameof(Strings.Resources.Photo_ToneBt2446ADescription),
        VideoSdrToneMappingAlgorithm.Clip => nameof(Strings.Resources.Photo_ToneClipDescription),
        VideoSdrToneMappingAlgorithm.Mobius => nameof(Strings.Resources.Photo_ToneMobiusDescription),
        VideoSdrToneMappingAlgorithm.Reinhard => nameof(Strings.Resources.Photo_ToneReinhardDescription),
        VideoSdrToneMappingAlgorithm.Hable => nameof(Strings.Resources.Photo_ToneHableDescription),
        VideoSdrToneMappingAlgorithm.Gamma => nameof(Strings.Resources.Photo_ToneGammaDescription),
        VideoSdrToneMappingAlgorithm.Linear => nameof(Strings.Resources.Photo_ToneLinearDescription),
        _ => nameof(Strings.Resources.Photo_ToneAutomaticDescription),
    });

    public ObservableCollection<ToneParameterViewModel> Parameters { get; } = [];

    /// <summary>The options being edited, or null to show <see cref="AutomaticOptions"/> until the first change.</summary>
    public VideoSdrToneMappingOptions? Options
    {
        get => options;
        set
        {
            if (options == value)
                return;
            options = value;
            Load();
        }
    }

    /// <summary>What the renderer chooses on its own, shown while <see cref="Options"/> is null.</summary>
    public VideoSdrToneMappingOptions AutomaticOptions
    {
        get => automaticOptions;
        set
        {
            if (automaticOptions == value)
                return;
            automaticOptions = value;
            if (options is null)
                Load();
        }
    }

    /// <summary>What headroom and luminance resolve to while they follow the display.</summary>
    public VideoDisplayToneTarget DisplayTarget
    {
        get => displayTarget;
        set
        {
            if (displayTarget == value)
                return;
            displayTarget = value;
            Load();
        }
    }

    /// <summary>
    /// With a fixed target, whether it is HDR. Its peak is then set apart from the curve, so the
    /// BT.2446A target luminance is not offered.
    /// </summary>
    public bool HdrTarget
    {
        get => hdrTarget;
        set
        {
            if (hdrTarget == value)
                return;
            hdrTarget = value;
            parameterAlgorithm = null;
            Load();
        }
    }

    private VideoSdrToneMappingOptions Current =>
        options ?? automaticOptions with { TargetHeadroom = null, TargetMaximumNits = null };

    private double DisplayReferenceWhite =>
        displayTarget.ReferenceWhiteNits ?? VideoSdrToneMappingOptions.DefaultReferenceWhiteNits;

    internal void OnParameterChanged(ToneParameterViewModel parameter, double value)
    {
        if (loading || parameter.IsLocked)
            return;
        double snapped = parameter.Snap(value);
        VideoSdrToneMappingOptions current = Editable();
        if (Math.Abs(parameter.Read(current) - snapped) < 1e-9)
            return;
        Apply(parameter.Write(current, snapped));
    }

    internal void ToggleLock(ToneParameterViewModel parameter)
    {
        VideoSdrToneMappingOptions current = Editable();
        Apply(parameter.LockKind switch
        {
            ToneParameterLock.Headroom => current with
            {
                TargetHeadroom = current.TargetHeadroom is null ? parameter.Snap(displayTarget.Headroom) : null,
            },
            ToneParameterLock.Luminance => current with
            {
                TargetMaximumNits = current.TargetMaximumNits is null ? parameter.Snap(displayTarget.PeakNits) : null,
            },
            ToneParameterLock.ReferenceWhite => current with
            {
                ReferenceWhiteNits = current.ReferenceWhiteNits is null ? parameter.Snap(DisplayReferenceWhite) : null,
            },
            _ => current,
        });
    }

    partial void OnSelectedCurveChanged(VideoSettingsOption? value)
    {
        if (loading || value is null || !Enum.TryParse(value.Value, out VideoSdrToneMappingAlgorithm algorithm) ||
            algorithm == Current.Algorithm)
            return;
        Apply(Current with { Algorithm = algorithm });
    }

    // The automatic choice reports Auto when nothing needs adapting; a change starts from the curve
    // the display would use once it does.
    private VideoSdrToneMappingOptions Editable() =>
        options is null && Current.Algorithm == VideoSdrToneMappingAlgorithm.Auto
            ? Current with
            {
                Algorithm = displayTarget.Headroom > 1
                    ? VideoSdrToneMappingAlgorithm.Bt2390
                    : VideoSdrToneMappingAlgorithm.Spline,
            }
            : Current;

    private void Apply(VideoSdrToneMappingOptions edited)
    {
        options = edited;
        Load();
        Edited?.Invoke(edited);
    }

    private void Load()
    {
        loading = true;
        try
        {
            VideoSdrToneMappingOptions current = Current;
            SelectedCurve = CurveOptions.FirstOrDefault(option => option.Value == current.Algorithm.ToString());
            if (parameterAlgorithm != current.Algorithm)
            {
                parameterAlgorithm = current.Algorithm;
                BuildParameters(current.Algorithm);
                OnPropertyChanged(nameof(CurveDescription));
            }

            foreach (ToneParameterViewModel parameter in Parameters)
            {
                if (fixedTarget)
                {
                    // An unset BT.2446A target maps the reference white to SDR white.
                    parameter.Load(parameter.LockKind == ToneParameterLock.Luminance && current.TargetMaximumNits is null
                        ? current.ResolveReferenceWhiteNits(null)
                        : parameter.Read(current), locked: false, lockable: false);
                    continue;
                }

                (bool locked, bool lockable, double displayValue) = parameter.LockKind switch
                {
                    ToneParameterLock.Headroom => (current.TargetHeadroom is null, true, displayTarget.Headroom),
                    ToneParameterLock.Luminance => (current.TargetMaximumNits is null, true, displayTarget.PeakNits),
                    // Offered only where the display reports a white, but kept while a profile follows it.
                    ToneParameterLock.ReferenceWhite => (current.ReferenceWhiteNits is null,
                        current.ReferenceWhiteNits is null || displayTarget.ReferenceWhiteNits is not null,
                        DisplayReferenceWhite),
                    _ => (false, false, 0d),
                };
                parameter.Load(locked ? displayValue : parameter.Read(current), locked, lockable);
            }
        }
        finally
        {
            loading = false;
        }
    }

    private void BuildParameters(VideoSdrToneMappingAlgorithm curve)
    {
        Parameters.Clear();
        if (curve == VideoSdrToneMappingAlgorithm.Bt2446A)
        {
            if (!fixedTarget || !hdrTarget)
                Add(nameof(Strings.Resources.Photo_ToneTargetLuminance), 50, 2000, 10, "0' nits'",
                    options => options.TargetMaximumNits ?? 400, (options, value) => options with { TargetMaximumNits = value },
                    ToneParameterLock.Luminance);
            return;
        }

        if (!fixedTarget)
            Add(nameof(Strings.Resources.Photo_ToneHeadroom), 1, 16, 0.1, "0.0'x'",
                options => options.TargetHeadroom ?? 4, (options, value) => options with { TargetHeadroom = value },
                ToneParameterLock.Headroom);
        Add(nameof(Strings.Resources.Photo_ToneReferenceWhite), 50, 500, 10, "0' nits'",
            options => options.ReferenceWhiteNits ?? VideoSdrToneMappingOptions.DefaultReferenceWhiteNits,
            (options, value) => options with { ReferenceWhiteNits = value }, ToneParameterLock.ReferenceWhite);
        if (curve == VideoSdrToneMappingAlgorithm.Bt2390)
        {
            Add(nameof(Strings.Resources.Photo_ToneKneeOffset), 0.5, 2, 0.05, "0.00",
                options => options.Bt2390KneeOffset, (options, value) => options with { Bt2390KneeOffset = (float)value });
            return;
        }

        if (curve is VideoSdrToneMappingAlgorithm.Mobius or VideoSdrToneMappingAlgorithm.Gamma)
        {
            Add(nameof(Strings.Resources.Photo_ToneKneePoint), 0.01, 0.99, 0.01, "0.00",
                options => options.LinearKnee, (options, value) => options with { LinearKnee = (float)value });
            return;
        }

        if (curve == VideoSdrToneMappingAlgorithm.Reinhard)
        {
            Add(nameof(Strings.Resources.Photo_ToneContrast), 0.01, 0.99, 0.01, "0.00",
                options => options.ReinhardContrast, (options, value) => options with { ReinhardContrast = (float)value });
            return;
        }

        if (curve == VideoSdrToneMappingAlgorithm.Linear)
        {
            Add(nameof(Strings.Resources.Photo_ToneExposure), 0.25, 4, 0.05, "0.00",
                options => options.LinearExposure, (options, value) => options with { LinearExposure = (float)value });
            return;
        }

        if (curve is VideoSdrToneMappingAlgorithm.Clip or VideoSdrToneMappingAlgorithm.Hable)
            return;

        Add(nameof(Strings.Resources.Photo_ToneKneeAdaptation), 0, 1, 0.05, "0.00",
            options => options.KneeAdaptation, (options, value) => options with { KneeAdaptation = (float)value });
        Add(nameof(Strings.Resources.Photo_ToneKneeMinimum), 0.01, 0.49, 0.01, "0.00",
            options => options.KneeMinimum, (options, value) => options with
            { KneeMinimum = (float)value, KneeDefault = Math.Max(options.KneeDefault, (float)value) });
        Add(nameof(Strings.Resources.Photo_ToneKneeMaximum), 0.51, 0.99, 0.01, "0.00",
            options => options.KneeMaximum, (options, value) => options with
            { KneeMaximum = (float)value, KneeDefault = Math.Min(options.KneeDefault, (float)value) });
        Add(nameof(Strings.Resources.Photo_ToneKneeDefault), 0.01, 0.99, 0.01, "0.00",
            options => options.KneeDefault, (options, value) => options with
            { KneeDefault = Math.Clamp((float)value, options.KneeMinimum, options.KneeMaximum) });
        Add(nameof(Strings.Resources.Photo_ToneSlopeTuning), 0, 10, 0.1, "0.0",
            options => options.SlopeTuning, (options, value) => options with { SlopeTuning = (float)value });
        Add(nameof(Strings.Resources.Photo_ToneSlopeOffset), 0, 1, 0.05, "0.00",
            options => options.SlopeOffset, (options, value) => options with { SlopeOffset = (float)value });
        Add(nameof(Strings.Resources.Photo_ToneContrast), 0, 1.5, 0.05, "0.00",
            options => options.SplineContrast, (options, value) => options with { SplineContrast = (float)value });
    }

    private void Add(string labelKey, double minimum, double maximum, double step, string format,
        Func<VideoSdrToneMappingOptions, double> read,
        Func<VideoSdrToneMappingOptions, double, VideoSdrToneMappingOptions> write,
        ToneParameterLock lockKind = ToneParameterLock.None) =>
        Parameters.Add(new ToneParameterViewModel(this, labelKey, minimum, maximum, step, format, read, write, lockKind));
}
