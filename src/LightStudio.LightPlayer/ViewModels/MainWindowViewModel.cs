namespace LightStudio.LightPlayer.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    public MainWindowViewModel(ShellViewModel shell)
    {
        Shell = shell;
    }

    public ShellViewModel Shell { get; }
}
