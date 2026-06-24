namespace LightStudio.LightPlayer.ViewModels.Pages;

public abstract class PageViewModelBase : ViewModelBase
{
    private string title = string.Empty;

    public string Title
    {
        get => title;
        protected set => SetProperty(ref title, value);
    }
}