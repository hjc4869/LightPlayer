using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.ViewModels;

public sealed partial class ShellViewModel
{
    [ObservableProperty]
    private bool groupingEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGroupedByName))]
    [NotifyPropertyChangedFor(nameof(IsGroupedByArtist))]
    [NotifyPropertyChangedFor(nameof(IsGroupedByAlbum))]
    [NotifyPropertyChangedFor(nameof(IsGroupedByCreationDay))]
    [NotifyPropertyChangedFor(nameof(IsGroupedByCreationMonth))]
    [NotifyPropertyChangedFor(nameof(IsGroupedByCreationYear))]
    [NotifyPropertyChangedFor(nameof(IsGroupedByCountry))]
    [NotifyPropertyChangedFor(nameof(IsGroupedByState))]
    [NotifyPropertyChangedFor(nameof(IsGroupedByCity))]
    private LibraryGroupField groupBy;

    public bool CanGroupItems => activeBrowseKind is not null;
    public bool CanGroupByName => SupportsGroup(LibraryGroupField.Name);
    public bool CanGroupByArtist => SupportsGroup(LibraryGroupField.Artist);
    public bool CanGroupByAlbum => SupportsGroup(LibraryGroupField.Album);
    public bool CanGroupByCreationDay => SupportsGroup(LibraryGroupField.CreationDay);
    public bool CanGroupByCreationMonth => SupportsGroup(LibraryGroupField.CreationMonth);
    public bool CanGroupByCreationYear => SupportsGroup(LibraryGroupField.CreationYear);
    public bool CanGroupByCountry => SupportsGroup(LibraryGroupField.Country);
    public bool CanGroupByState => SupportsGroup(LibraryGroupField.State);
    public bool CanGroupByCity => SupportsGroup(LibraryGroupField.City);
    public bool IsGroupedByName => GroupBy == LibraryGroupField.Name;
    public bool IsGroupedByArtist => GroupBy == LibraryGroupField.Artist;
    public bool IsGroupedByAlbum => GroupBy == LibraryGroupField.Album;
    public bool IsGroupedByCreationDay => GroupBy == LibraryGroupField.CreationDay;
    public bool IsGroupedByCreationMonth => GroupBy == LibraryGroupField.CreationMonth;
    public bool IsGroupedByCreationYear => GroupBy == LibraryGroupField.CreationYear;
    public bool IsGroupedByCountry => GroupBy == LibraryGroupField.Country;
    public bool IsGroupedByState => GroupBy == LibraryGroupField.State;
    public bool IsGroupedByCity => GroupBy == LibraryGroupField.City;

    private bool SupportsGroup(LibraryGroupField field) => activeBrowseKind is { } kind &&
        LibraryViewSettings.SupportedGroups(kind).Contains(field);

    [RelayCommand]
    private void ToggleGrouping()
    {
        if (CanGroupItems)
            GroupingEnabled = !GroupingEnabled;
    }

    [RelayCommand]
    private void SetGroupBy(LibraryGroupField field)
    {
        if (SupportsGroup(field))
            GroupBy = field;
    }

    partial void OnGroupingEnabledChanged(bool value) => PersistViewSettings();
    partial void OnGroupByChanged(LibraryGroupField value) => PersistViewSettings();

    private void UpdateGroupingVisibility()
    {
        OnPropertyChanged(nameof(CanGroupItems));
        OnPropertyChanged(nameof(CanGroupByName));
        OnPropertyChanged(nameof(CanGroupByArtist));
        OnPropertyChanged(nameof(CanGroupByAlbum));
        OnPropertyChanged(nameof(CanGroupByCreationDay));
        OnPropertyChanged(nameof(CanGroupByCreationMonth));
        OnPropertyChanged(nameof(CanGroupByCreationYear));
        OnPropertyChanged(nameof(CanGroupByCountry));
        OnPropertyChanged(nameof(CanGroupByState));
        OnPropertyChanged(nameof(CanGroupByCity));
    }
}
