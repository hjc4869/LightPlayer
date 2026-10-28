using System.Collections.Generic;
using Avalonia;

namespace LightStudio.LightPlayer.ViewModels.Pages;

public sealed class SettingsNavigationItemViewModel : ViewModelBase
{
    private readonly string titleKey;
    private bool isFirstInGroup;
    private bool isSelected;

    public SettingsNavigationItemViewModel(string titleKey, object content, string iconKey = "IconSettings")
    {
        this.titleKey = titleKey;
        Content = content;
        IconKey = iconKey;
    }

    public SettingsNavigationItemViewModel(SettingsSectionViewModel category, params SettingsNavigationItemViewModel[] children)
    {
        titleKey = category.Key;
        Children = children;
        for (var index = 0; index < children.Length; index++)
        {
            children[index].isFirstInGroup = index == 0;
            children[index].IsLastInGroup = index == children.Length - 1;
        }
    }

    public string Key => titleKey;
    public string Title => Localized(titleKey);
    public string IconKey { get; } = "IconSettings";
    public object? Content { get; }
    public IReadOnlyList<SettingsNavigationItemViewModel> Children { get; } = [];
    public bool IsCategory => Content is null;
    public bool IsLastInGroup { get; private set; } = true;

    /// <summary>Whether this is the open section, highlighted in the sidebar.</summary>
    public bool IsSelected
    {
        get => isSelected;
        set => SetProperty(ref isSelected, value);
    }

    public CornerRadius GroupCornerRadius => new(
        isFirstInGroup ? 8 : 0,
        isFirstInGroup ? 8 : 0,
        IsLastInGroup ? 8 : 0,
        IsLastInGroup ? 8 : 0);
}

public sealed class SettingsContentSectionViewModel(string titleKey, object content)
    : SettingsSectionViewModel(titleKey, string.Empty)
{
    public object Content { get; } = content;
}

public sealed class DisplaySettingsViewModel(VideoSettingsSectionViewModel video) : ViewModelBase
{
    public VideoSettingsSectionViewModel Video { get; } = video;
}

public sealed class CustomFiltersSettingsViewModel(VideoSettingsSectionViewModel video) : ViewModelBase
{
    public VideoSettingsSectionViewModel Video { get; } = video;
}
