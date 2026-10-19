using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using LightMediaRenderer.Contracts;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Strings;
using LightStudio.LightPlayer.ViewModels.Pages;
using ToneResources = LightStudio.LightPlayer.Strings.Resources;

namespace LightStudio.LightPlayer.Views.Pages;

public partial class PhotoViewerPageView : ILocalizedTextHost
{
    private readonly List<Action<VideoSdrToneMappingOptions>> toneParameterUpdates = [];
    private bool updatingToneMapping;
    private bool savingToneMapping;
    private int loadedToneCurve = -1;

    private Flyout HdrFlyout => (Flyout)HdrButton.Flyout!;

    private bool CanTargetHdr => ViewModel?.EnableHdrPassthrough == true &&
        Photo.IsExtendedRangeOutput && Photo.SupportsHdrOutput;

    private static VideoSdrToneMappingOptions EditableToneDefaults => new()
    {
        TargetHeadroom = 4,
        TargetMaximumNits = 400,
    };

    void ILocalizedTextHost.OnLocalizedTextChanged()
    {
        loadedToneCurve = -1;
        updatingToneMapping = true;
        ToneCurveBox.SelectedIndex = -1;
        updatingToneMapping = false;
        UpdateHdrControls();
    }

    private void OnPhotoModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(PhotoViewerPageViewModel.NavigationParameter))
            HdrFlyout.Hide();
        if (args.PropertyName is nameof(PhotoViewerPageViewModel.HasAbsoluteLuminance) or
            nameof(PhotoViewerPageViewModel.EnableHdrPassthrough) or nameof(PhotoViewerPageViewModel.ToneMapping) or
            nameof(PhotoViewerPageViewModel.HasPreferredToneMapping))
            UpdateHdrControls();
    }

    private void UpdateHdrControls()
    {
        if (HdrButton is null)
            return;
        HdrButton.IsVisible = ViewModel?.HasAbsoluteLuminance == true;
        if (!HdrButton.IsVisible)
            HdrFlyout.Hide();
        if (HdrFlyout.IsOpen)
            LoadToneMappingControls();
    }

    private void OnHdrFlyoutOpening(object? sender, EventArgs args)
    {
        UpdateToneMappingPopupSize();
        LoadToneMappingControls();
    }

    private void UpdateToneMappingPopupSize()
    {
        if (ToneMappingContent is null)
            return;
        double availableWidth = Bounds.Width - 64;
        if (HdrButton.TranslatePoint(default, this) is { } position)
        {
            double center = position.X + HdrButton.Bounds.Width / 2;
            availableWidth = Math.Min(availableWidth, 2 * Math.Min(center, Bounds.Width - center) - 48);
        }
        ToneMappingContent.Width = Math.Max(160, Math.Min(340, availableWidth));
        ToneMappingScroll.MaxHeight = Math.Max(80, Bounds.Height - 100);
    }

    private void LoadToneMappingControls()
    {
        if (ViewModel is not { } model)
            return;
        updatingToneMapping = true;
        try
        {
            int curve = model.ToneMapping is { } selected ? (int)selected.Algorithm : 0;
            ToneCurveBox.SelectedIndex = curve;
            var options = model.ToneMapping ?? Photo.AutomaticToneMapping;
            if (loadedToneCurve != (int)options.Algorithm)
            {
                loadedToneCurve = (int)options.Algorithm;
                BuildToneParameters(options.Algorithm);
            }

            foreach (var update in toneParameterUpdates)
                update(options);
            ToneResetButton.IsEnabled = model.ToneMapping is not null;
            ToneSaveButton.IsEnabled = !savingToneMapping && model.ToneMapping is not null;
            ToneClearButton.IsEnabled = !savingToneMapping && model.HasPreferredToneMapping;
        }
        finally
        {
            updatingToneMapping = false;
        }
    }

    private void OnToneCurveChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (updatingToneMapping || ViewModel is not { } model || ToneCurveBox.SelectedIndex < 0)
            return;
        model.ToneMapping = ToneCurveBox.SelectedIndex == 0 ? null :
            (model.ToneMapping ?? EditableToneDefaults) with { Algorithm = (VideoSdrToneMappingAlgorithm)ToneCurveBox.SelectedIndex };
    }

    private void BuildToneParameters(VideoSdrToneMappingAlgorithm curve)
    {
        ToneParameterRows.Children.Clear();
        toneParameterUpdates.Clear();
        if (curve != VideoSdrToneMappingAlgorithm.Bt2446A)
        {
            AddToneParameter(nameof(ToneResources.Photo_ToneHeadroom), 1, 16, 0.1, "0.0'x'",
                options => options.TargetHeadroom ?? 4, (options, value) => options with { TargetHeadroom = value },
                () => CanTargetHdr, headroom: true);
        }
        if (curve == VideoSdrToneMappingAlgorithm.Bt2446A)
        {
            AddToneParameter(nameof(ToneResources.Photo_ToneTargetLuminance), 50, 2000, 10, "0' nits'",
                options => options.TargetMaximumNits ?? 400, (options, value) => options with { TargetMaximumNits = value });
            return;
        }

        AddToneParameter(nameof(ToneResources.Photo_ToneReferenceWhite), 50, 500, 10, "0' nits'",
            options => options.ReferenceWhiteNits, (options, value) => options with { ReferenceWhiteNits = value });
        if (curve == VideoSdrToneMappingAlgorithm.Auto)
            return;
        if (curve == VideoSdrToneMappingAlgorithm.Bt2390)
        {
            AddToneParameter(nameof(ToneResources.Photo_ToneKneeOffset), 0.5, 2, 0.05, "0.00",
                options => options.Bt2390KneeOffset, (options, value) => options with { Bt2390KneeOffset = (float)value });
            return;
        }

        AddToneParameter(nameof(ToneResources.Photo_ToneKneeAdaptation), 0, 1, 0.05, "0.00",
            options => options.KneeAdaptation, (options, value) => options with { KneeAdaptation = (float)value });
        AddToneParameter(nameof(ToneResources.Photo_ToneKneeMinimum), 0.01, 0.49, 0.01, "0.00",
            options => options.KneeMinimum, (options, value) => options with
            { KneeMinimum = (float)value, KneeDefault = Math.Max(options.KneeDefault, (float)value) });
        AddToneParameter(nameof(ToneResources.Photo_ToneKneeMaximum), 0.51, 0.99, 0.01, "0.00",
            options => options.KneeMaximum, (options, value) => options with
            { KneeMaximum = (float)value, KneeDefault = Math.Min(options.KneeDefault, (float)value) });
        AddToneParameter(nameof(ToneResources.Photo_ToneKneeDefault), 0.01, 0.99, 0.01, "0.00",
            options => options.KneeDefault, (options, value) => options with
            { KneeDefault = Math.Clamp((float)value, options.KneeMinimum, options.KneeMaximum) });
        AddToneParameter(nameof(ToneResources.Photo_ToneSlopeTuning), 0, 10, 0.1, "0.0",
            options => options.SlopeTuning, (options, value) => options with { SlopeTuning = (float)value });
        AddToneParameter(nameof(ToneResources.Photo_ToneSlopeOffset), 0, 1, 0.05, "0.00",
            options => options.SlopeOffset, (options, value) => options with { SlopeOffset = (float)value });
        AddToneParameter(nameof(ToneResources.Photo_ToneContrast), 0, 1.5, 0.05, "0.00",
            options => options.SplineContrast, (options, value) => options with { SplineContrast = (float)value });
    }

    private void AddToneParameter(string key, double minimum, double maximum, double step, string format,
        Func<VideoSdrToneMappingOptions, double> read,
        Func<VideoSdrToneMappingOptions, double, VideoSdrToneMappingOptions> write,
        Func<bool>? enabled = null, bool headroom = false)
    {
        var label = new TextBlock
        {
            Text = LocalizedStrings.Instance[key], TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 8, 0),
        };
        var value = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider
        {
            Minimum = minimum, Maximum = maximum, SmallChange = step, LargeChange = step * 5,
            TickFrequency = step, IsSnapToTickEnabled = true,
        };
        AutomationProperties.SetName(slider, label.Text);
        toneParameterUpdates.Add(options =>
        {
            slider.IsEnabled = enabled?.Invoke() != false;
            double currentValue = headroom && ToneCurveBox.SelectedIndex != 0 && !CanTargetHdr ? 1 : read(options);
            slider.Value = currentValue;
            value.Text = currentValue.ToString(format);
        });
        slider.PropertyChanged += (_, args) =>
        {
            if (args.Property != RangeBase.ValueProperty || updatingToneMapping || !slider.IsEnabled || ViewModel is not { } model)
                return;
            double snapped = Math.Clamp(Math.Round((slider.Value - minimum) / step) * step + minimum, minimum, maximum);
            var options = model.ToneMapping ?? Photo.AutomaticToneMapping;
            if (options.Algorithm == VideoSdrToneMappingAlgorithm.Auto)
                options = options with { Algorithm = CanTargetHdr ? VideoSdrToneMappingAlgorithm.Bt2390 : VideoSdrToneMappingAlgorithm.Spline };
            model.ToneMapping = write(options, snapped);
            LoadToneMappingControls();
        };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        Grid.SetColumn(value, 1);
        Grid.SetRow(slider, 1);
        Grid.SetColumnSpan(slider, 2);
        row.Children.Add(label);
        row.Children.Add(value);
        row.Children.Add(slider);
        ToneParameterRows.Children.Add(row);
    }

    private void OnToneResetClicked(object? sender, RoutedEventArgs args)
    {
        if (ViewModel is { } model)
            model.ToneMapping = null;
    }

    private async void OnToneSaveClicked(object? sender, RoutedEventArgs args) =>
        await PersistToneMappingAsync(clear: false);

    private async void OnToneClearClicked(object? sender, RoutedEventArgs args) =>
        await PersistToneMappingAsync(clear: true);

    private async System.Threading.Tasks.Task PersistToneMappingAsync(bool clear)
    {
        if (savingToneMapping || ViewModel is not { } model)
            return;
        savingToneMapping = true;
        LoadToneMappingControls();
        try
        {
            await AppErrorReporter.GuardAsync(() => model.SaveToneMappingPreferenceAsync(clear));
        }
        finally
        {
            savingToneMapping = false;
            LoadToneMappingControls();
        }
    }
}