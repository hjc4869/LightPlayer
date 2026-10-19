using Avalonia.Controls;

namespace LightStudio.LightPlayer.Controls;

public partial class PageSearchBar : UserControl
{
    public PageSearchBar() => InitializeComponent();

    internal bool IsQueryFocused => QueryBox.IsFocused;

    internal void FocusQuery()
    {
        QueryBox.Focus();
        QueryBox.SelectAll();
    }
}