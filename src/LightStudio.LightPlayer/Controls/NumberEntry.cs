using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;

namespace LightStudio.LightPlayer.Controls;

/// <summary>
/// A number field that never hands a half-typed entry to the setting behind it. Bind
/// <see cref="Number"/> rather than <c>Value</c>: an empty or unreadable field leaves the bound
/// number as it was and puts it back once focus moves on, and a number outside the allowed range is
/// pulled into it instead of being flagged as an error.
/// </summary>
/// <remarks>
/// The underlying <see cref="NumericUpDown.Value"/> is nullable because the field is empty for as
/// long as it takes to clear it and type a new number. Binding a plain number to it directly is what
/// raises "could not convert '(null)'" under the field while the user is still typing.
/// </remarks>
public sealed class NumberEntry : NumericUpDown
{
    public static readonly StyledProperty<decimal> NumberProperty =
        AvaloniaProperty.Register<NumberEntry, decimal>(
            nameof(Number),
            defaultBindingMode: BindingMode.TwoWay);

    public NumberEntry() => ClipValueToMinMax = true;

    public decimal Number
    {
        get => GetValue(NumberProperty);
        set => SetValue(NumberProperty, value);
    }

    // The theme is keyed on the base type; without this the field would come up unstyled.
    protected override Type StyleKeyOverride => typeof(NumericUpDown);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == NumberProperty)
        {
            SetCurrentValue(ValueProperty, change.GetNewValue<decimal>());
        }
        else if (change.Property == ValueProperty && change.GetNewValue<decimal?>() is { } value)
        {
            SetCurrentValue(NumberProperty, value);
        }
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);

        if (Value is null)
        {
            SetCurrentValue(ValueProperty, Number);
        }
    }
}
