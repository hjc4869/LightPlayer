namespace LightStudio.LightPlayer.ViewModels;

/// <summary>
/// Implemented by a page that can change what it shows without a navigation, such as the video
/// player moving to another entry of its own list. The shell follows <see cref="NavigationParameter"/>
/// so its location - and any host address mirroring it, like the browser head's URL hash - keeps
/// addressing what is actually on screen, without adding a history entry.
/// </summary>
public interface INavigationParameterSource
{
    /// <summary>
    /// The parameter the page would be reopened with as it stands, or null while it shows nothing.
    /// Implementers must raise <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/>
    /// for this property whenever it changes.
    /// </summary>
    object? NavigationParameter { get; }
}
