using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.ViewModels.Pages;

public abstract class PageViewModelBase : ViewModelBase
{
    private string title = string.Empty;
    private string? titleKey;
    private bool isNarrow;
    private bool hasShellBackButton;
    private bool hasShellPathMenu;

    public virtual PageSearchModel PageSearch { get; } = new();

    public string Title
    {
        get => titleKey is { } key ? Localized(key) : title;
        protected set
        {
            titleKey = null;
            SetProperty(ref title, value);
        }
    }

    /// <summary>
    /// Sets <see cref="Title"/> from a resource key (use <c>nameof(Resources.Key)</c>) so it
    /// follows the interface language instead of freezing the text at construction.
    /// </summary>
    protected string? TitleKey
    {
        get => titleKey;
        set
        {
            titleKey = value;
            OnPropertyChanged(nameof(Title));
        }
    }

    /// <summary>
    /// True while the hosting shell is in its narrow (mobile) layout. Set by the shell as the
    /// viewport crosses its narrow breakpoint; pages bind to it to tighten their horizontal content
    /// insets and to hide wide-only chrome (such as the songs column header) on narrow layouts.
    /// </summary>
    public bool IsNarrow
    {
        get => isNarrow;
        set
        {
            if (SetProperty(ref isNarrow, value))
            {
                OnIsNarrowChanged(value);
            }
        }
    }

    /// <summary>
    /// Called when <see cref="IsNarrow"/> changes. Pages override this to update chrome that depends
    /// on the shell's layout width, such as their content insets.
    /// </summary>
    protected virtual void OnIsNarrowChanged(bool value)
    {
    }

    /// <summary>
    /// True while the shell offers its own back button (the narrow top bar or the Apple toolbar). Set by
    /// the shell; pages hide their own back buttons while it is set.
    /// </summary>
    public bool HasShellBackButton
    {
        get => hasShellBackButton;
        set
        {
            if (SetProperty(ref hasShellBackButton, value))
            {
                OnHasShellBackButtonChanged(value);
            }
        }
    }

    protected virtual void OnHasShellBackButtonChanged(bool value)
    {
    }

    /// <summary>
    /// True while the shell lists the folder path in a menu on its page title (the native Apple UI
    /// style). Set by the shell; pages hide their address bars while it is set.
    /// </summary>
    public bool HasShellPathMenu
    {
        get => hasShellPathMenu;
        set
        {
            if (SetProperty(ref hasShellPathMenu, value))
            {
                OnHasShellPathMenuChanged(value);
            }
        }
    }

    protected virtual void OnHasShellPathMenuChanged(bool value)
    {
    }
}