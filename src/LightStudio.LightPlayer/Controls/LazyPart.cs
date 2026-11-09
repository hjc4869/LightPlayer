using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Metadata;

namespace LightStudio.LightPlayer.Controls;

/// <summary>
/// Builds its content the first time <see cref="IsActive"/> turns true and stays collapsed until
/// then, so the parts of an item template that most items never show (hover actions, selection
/// check boxes, collages) cost nothing while a grid realizes hundreds of tiles. The content keeps
/// its own visibility rules once built; the part itself is visible whenever it is active.
/// </summary>
public sealed class LazyPart : Decorator
{
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<LazyPart, bool>(nameof(IsActive));

    public static readonly StyledProperty<ITemplate<Control?>?> TemplateProperty =
        AvaloniaProperty.Register<LazyPart, ITemplate<Control?>?>(nameof(Template));

    public LazyPart()
    {
        IsVisible = false;
    }

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    [Content]
    public ITemplate<Control?>? Template
    {
        get => GetValue(TemplateProperty);
        set => SetValue(TemplateProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsActiveProperty)
        {
            var active = change.GetNewValue<bool>();
            if (active)
            {
                Build();
            }

            IsVisible = active;
        }
        else if (change.Property == TemplateProperty && IsActive)
        {
            Build();
        }
    }

    private void Build()
    {
        if (Child is null && Template?.Build() is { } content)
        {
            Child = content;
        }
    }
}
