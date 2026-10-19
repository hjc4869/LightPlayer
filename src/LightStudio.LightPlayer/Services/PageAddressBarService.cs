using CommunityToolkit.Mvvm.ComponentModel;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.ViewModels.Pages;

namespace LightStudio.LightPlayer.Services;

/// <summary>Selects the shell-wide address bar without taking ownership of the page's lifetime.</summary>
public sealed class PageAddressBarService : ObservableObject
{
    private PageViewModelBase? page;

    public PageViewModelBase? Page => page;

    public PageAddressBarModel? Current => (page as IPageAddressBarSource)?.AddressBar;

    internal void SetPage(PageViewModelBase value)
    {
        if (SetProperty(ref page, value is IPageAddressBarSource ? value : null, nameof(Page)))
        {
            OnPropertyChanged(nameof(Current));
        }
    }
}