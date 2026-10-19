using Avalonia.Controls;
using Avalonia.Controls.Generators;
using Avalonia.Controls.Presenters;

namespace LightStudio.LightPlayer.Controls;

/// <summary>
/// Prepares a pooled container that was left prepared for its previous item.
/// </summary>
/// <remarks>
/// <see cref="ItemsControl"/> assigns a container's content only while it is unset, so preparation alone
/// would leave the previous item as content behind the new data context. The presenter rebuilds its child
/// from that content when it is attached again, displaying the previous item. Pointing the content at the
/// new item lets the presenter keep the control its recycling template built and rebind it.
/// </remarks>
internal static class RecycledContainer
{
    public static void Prepare(ItemContainerGenerator generator, Control container, object? item, int index)
    {
        if (container is ContentPresenter presenter && !ReferenceEquals(presenter.Content, item))
            presenter.SetCurrentValue(ContentPresenter.ContentProperty, item);
        else if (container is ContentControl control && !ReferenceEquals(control.Content, item))
            control.SetCurrentValue(ContentControl.ContentProperty, item);
        generator.PrepareItemContainer(container, item, index);
    }
}
