using CommunityToolkit.Mvvm.ComponentModel;

namespace LightStudio.LightPlayer.Models;

public abstract class SelectionItemModel : ObservableObject
{
    private MultiSelectionModel? selection;
    private bool isSelected;

    public MultiSelectionModel? Selection
    {
        get => selection;
        internal set
        {
            if (SetProperty(ref selection, value))
            {
                OnPropertyChanged(nameof(CanSelect));
                NotifySelectionModeChanged();
            }
        }
    }

    public bool CanSelect => Selection is not null;

    public bool IsSelectionMode => Selection?.IsActive == true;

    public bool IsSelected
    {
        get => isSelected;
        internal set => SetProperty(ref isSelected, value);
    }

    internal void NotifySelectionModeChanged() => OnPropertyChanged(nameof(IsSelectionMode));
}