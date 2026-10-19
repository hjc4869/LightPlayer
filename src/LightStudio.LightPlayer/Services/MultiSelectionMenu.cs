using Avalonia.Controls;
using Avalonia.Input;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.Services;

public static class MultiSelectionMenu
{
    public static void Show(MultiSelectionModel selection, Control target, bool includeSelectionCommands = false)
    {
        selection.UpdateActions();
        var flyout = new MenuFlyout();
        foreach (var action in selection.Actions)
        {
            if (action.IsAvailable)
            {
                flyout.Items.Add(new MenuItem
                {
                    Header = LocalizedStrings.Instance[action.ResourceKey],
                    Command = action.Command,
                });
            }
        }

        if (includeSelectionCommands)
        {
            if (flyout.Items.Count > 0)
            {
                flyout.Items.Add(new Separator());
            }

            flyout.Items.Add(new MenuItem { Header = Resources.QueuePane_SelectAll, Command = selection.SelectAllCommand });
            flyout.Items.Add(new MenuItem { Header = Resources.QueuePane_UnselectAll, Command = selection.SelectNoneCommand });
            flyout.Items.Add(new MenuItem { Header = Resources.Selection_Exit, Command = selection.ExitCommand });
        }

        if (flyout.Items.Count > 0 && !NativeContextMenu.TryShow(flyout, target))
        {
            flyout.ShowAt(target);
        }
    }

    public static void HandleKey(MultiSelectionModel selection, KeyEventArgs args)
    {
        if (!selection.IsActive || args.Handled)
        {
            return;
        }

        if (args.Key == Key.Escape)
        {
            selection.Exit();
            args.Handled = true;
        }
        else if (args.Key == Key.A && (args.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
        {
            if (selection.SelectAllCommand.CanExecute(null))
            {
                selection.SelectAllCommand.Execute(null);
            }

            args.Handled = true;
        }
    }
}