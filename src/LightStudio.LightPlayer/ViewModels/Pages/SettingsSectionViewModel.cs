namespace LightStudio.LightPlayer.ViewModels.Pages;

public abstract class SettingsSectionViewModel : ViewModelBase
{
    protected SettingsSectionViewModel(string title, string description)
    {
        Title = title;
        Description = description;
    }

    public string Title { get; }

    public string Description { get; }
}