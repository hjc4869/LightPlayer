using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace LightStudio.LightPlayer.Models;

public sealed class MultiSelectionAction : ObservableObject
{
    private readonly Func<IReadOnlyList<SelectionItemModel>, bool>? canApply;
    private bool isAvailable;

    internal MultiSelectionAction(
        MultiSelectionModel selection,
        string resourceKey,
        Func<IReadOnlyList<SelectionItemModel>, Task> execute,
        Func<IReadOnlyList<SelectionItemModel>, bool>? canApply)
    {
        ResourceKey = resourceKey;
        this.canApply = canApply;
        Command = new AsyncRelayCommand(
            () => selection.ExecuteAsync(execute),
            () => IsAvailable && !selection.IsBusy);
    }

    public string ResourceKey { get; }

    public bool IsAvailable
    {
        get => isAvailable;
        private set => SetProperty(ref isAvailable, value);
    }

    public IAsyncRelayCommand Command { get; }

    internal void Update(IReadOnlyList<SelectionItemModel> items)
    {
        IsAvailable = items.Count > 0 && (canApply?.Invoke(items) ?? true);
        Command.NotifyCanExecuteChanged();
    }
}