using Avalonia;

namespace LightStudio.LightPlayer.ViewModels.Pages;

public abstract class SettingsSectionViewModel : ViewModelBase
{
    /// <summary>Inset a section applies to its own content on the wide (two-pane) settings layout.</summary>
    private static readonly Thickness WideContentMargin = new(24, 32, 24, 32);

    /// <summary>Inset a section applies to its own content on the narrow (mobile) settings layout.</summary>
    private static readonly Thickness NarrowContentMargin = new(15, 5, 15, 5);

    private readonly string titleKey;
    private readonly string descriptionKey;
    private bool isNarrow;
    private bool hostProvidesContentMargin;

    /// <summary>
    /// Takes resource keys (use <c>nameof(Resources.Key)</c>) rather than resolved text so the
    /// section headings follow the interface language.
    /// </summary>
    protected SettingsSectionViewModel(string titleKey, string descriptionKey)
    {
        this.titleKey = titleKey;
        this.descriptionKey = descriptionKey;
    }

    public string Title => Localized(titleKey);

    /// <summary>Stable identity of the section, used to record the open section in the shell's location.</summary>
    public string Key => titleKey;

    public string Description => Localized(descriptionKey);

    /// <summary>
    /// True while the hosting shell is in its narrow (mobile) layout. Propagated by
    /// <see cref="SettingsPageViewModel"/>; tightens <see cref="ContentMargin"/>.
    /// </summary>
    public bool IsNarrow
    {
        get => isNarrow;
        set
        {
            if (SetProperty(ref isNarrow, value))
            {
                OnPropertyChanged(nameof(ContentMargin));
            }
        }
    }

    /// <summary>
    /// Set by hosts that already inset the section themselves - the first-run setup window stacks
    /// several sections inside one padded scroll viewer, so a section adding its own margin there
    /// would sit narrower than its siblings. Clears <see cref="ContentMargin"/>.
    /// </summary>
    public bool HostProvidesContentMargin
    {
        get => hostProvidesContentMargin;
        set
        {
            if (SetProperty(ref hostProvidesContentMargin, value))
            {
                OnPropertyChanged(nameof(ContentMargin));
            }
        }
    }

    /// <summary>
    /// Inset applied to the content inside a section view's scroll viewer (rather than to the scroll
    /// viewer itself, so the scroll bar stays hard against the pane edge). The settings detail pane
    /// carries no padding of its own, so every top-level section view binds its margin to this.
    /// </summary>
    public Thickness ContentMargin => HostProvidesContentMargin
        ? default
        : IsNarrow ? NarrowContentMargin : WideContentMargin;
}
