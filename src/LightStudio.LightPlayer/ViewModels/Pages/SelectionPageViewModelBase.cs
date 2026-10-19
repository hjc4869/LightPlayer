using System.ComponentModel;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.ViewModels.Pages;

public abstract class SelectionPageViewModelBase : PageViewModelBase, IInPageBackHandler
{
    protected SelectionPageViewModelBase() => Selection.PropertyChanged += OnSelectionChanged;

    public MultiSelectionModel Selection { get; } = new();

    public virtual bool CanHandleBack => Selection.IsActive;

    public virtual bool TryHandleBack()
    {
        if (!Selection.IsActive)
        {
            return false;
        }

        Selection.Exit();
        return true;
    }

    private void OnSelectionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MultiSelectionModel.IsActive))
        {
            OnPropertyChanged(nameof(CanHandleBack));
        }
    }
}