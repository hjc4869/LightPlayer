using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using LightStudio.LightPlayer.Animations;

namespace LightStudio.LightPlayer.Views.Pages;

public partial class SettingsPageView : UserControl
{
    private readonly EntrancePageTransition sectionTransition = new();

    public static IValueConverter HasSwitchContent { get; } =
        new FuncValueConverter<object?, bool>(content => content is not null && content is not string { Length: 0 });

    static SettingsPageView()
    {
        // Workaround Android ComboBox offset issue
        // Dropdown-item BringIntoView requests escape into the page scroller.
        // Android’s overlay popups share the window’s visual root, allowing
        // this incorrect scrolling; native desktop popups don’t.
        RequestBringIntoViewEvent.AddClassHandler<ComboBox>((comboBox, e) =>
        {
            if (e.TargetObject is { } target
                && target != comboBox
                && !comboBox.IsVisualAncestorOf(target)
                && comboBox.FindAncestorOfType<SettingsPageView>() is not null)
            {
                e.Handled = true;
            }
        }, RoutingStrategies.Bubble);
    }

    public SettingsPageView()
    {
        InitializeComponent();
    }

    // The first section arrives with the page itself, so only later switches animate.
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        SidebarDetailHost.PageTransition = sectionTransition;
    }

}
