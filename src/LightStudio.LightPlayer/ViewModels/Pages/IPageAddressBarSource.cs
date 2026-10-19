using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>A page whose address bar and find overlay are presented by the shell.</summary>
public interface IPageAddressBarSource
{
    PageAddressBarModel AddressBar { get; }
}