using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.ViewModels;

namespace LightStudio.LightPlayer.Controls;

public partial class SearchBox : UserControl
{
    public SearchBox()
    {
        InitializeComponent();
    }

    /// <summary>Moves keyboard focus into the search text input.</summary>
    public void FocusInput()
    {
        var textBox = SearchAuto.FindDescendantOfType<TextBox>();
        if (textBox is not null)
        {
            textBox.Focus();
        }
        else
        {
            SearchAuto.Focus();
        }
    }

    /// <summary>
    /// Navigates when the user clicks a suggestion row. Reacting to the row's own pointer release
    /// (rather than the AutoCompleteBox SelectedItem) avoids the control's text-completion behaviour,
    /// which selects an item whenever the typed text matches a title and would otherwise navigate
    /// while the user is still typing.
    /// </summary>
    private void OnSuggestionPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left
            || sender is not Control { DataContext: SearchResultModel suggestion }
            || DataContext is not SearchBoxViewModel viewModel)
        {
            return;
        }

        // Let the AutoCompleteBox finish committing and closing its drop-down before navigating.
        Dispatcher.UIThread.Post(() => viewModel.ChooseSuggestion(suggestion));
    }
}
