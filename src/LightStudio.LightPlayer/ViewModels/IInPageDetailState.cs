namespace LightStudio.LightPlayer.ViewModels;

/// <summary>
/// Implemented by a master-detail page whose open detail is part of the shell's navigation state
/// rather than a page of its own. Lets a host history (the browser head's URL) record which detail
/// is open and reopen it after a reload or a forward navigation.
/// </summary>
public interface IInPageDetailState
{
    /// <summary>
    /// Opaque, stable key of the open detail, or null when the page shows its list. Implementers
    /// must raise <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/> for
    /// this property whenever the selection changes.
    /// </summary>
    string? DetailKey { get; }

    /// <summary>
    /// Reopens the detail recorded in a navigation entry; null closes it. A key whose item has not
    /// been loaded yet is applied once the page's items arrive.
    /// </summary>
    void RestoreDetail(string? key);
}
