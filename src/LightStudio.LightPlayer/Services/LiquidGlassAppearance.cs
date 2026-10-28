using System.ComponentModel;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// The liquid glass appearance that every backdrop material in the app binds to, so changes reach
/// live materials without refreshing application resources.
/// </summary>
public sealed class LiquidGlassAppearance : INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs FrostChangedArgs = new(nameof(Frost));
    private double frost;

    private LiquidGlassAppearance()
    {
    }

    public static LiquidGlassAppearance Instance { get; } = new();

    /// <summary>How frosted liquid glass appears, from 0 for clear to 1 for frosted.</summary>
    public double Frost
    {
        get => frost;
        set
        {
            if (frost == value)
                return;

            frost = value;
            PropertyChanged?.Invoke(this, FrostChangedArgs);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
