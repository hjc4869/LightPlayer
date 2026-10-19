using System;
using System.Collections.Generic;
using System.Windows.Input;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.ViewModels;
using LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.Models;

public sealed record PageAddressBarItem(string Name, object? Parameter, ICommand Command);

/// <summary>Page-owned navigation commands and layout measurements for the shell address bar.</summary>
public sealed partial class PageAddressBarModel(
    ICommand goUpCommand,
    ICommand refreshCommand,
    string goUpTextKey) : ViewModelBase
{
    private double height;
    private double topOffset;

    [ObservableProperty]
    private bool isVisible;

    [ObservableProperty]
    private bool canGoUp;

    [ObservableProperty]
    private IReadOnlyList<PageAddressBarItem> breadcrumb = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HostMargin))]
    private Thickness margin = new(32, 12, 32, 0);

    public ICommand GoUpCommand { get; } = goUpCommand;

    public ICommand RefreshCommand { get; } = refreshCommand;

    public MediaSortModel? Sorting { get; init; }

    public string GoUpText => Localized(goUpTextKey);

    public double Height
    {
        get => height;
        internal set => SetProperty(ref height, value);
    }

    public Thickness HostMargin => new(Margin.Left, Margin.Top + topOffset, Margin.Right, Margin.Bottom);

    internal double TopOffset
    {
        get => topOffset;
        set
        {
            if (SetProperty(ref topOffset, value))
            {
                OnPropertyChanged(nameof(HostMargin));
            }
        }
    }
}

public sealed class MediaSortModel : ViewModelBase
{
    public MediaSortModel(Action<LibraryViewSettings> apply)
    {
        SetSortCommand = new RelayCommand<LibrarySortField>(field =>
            apply(Settings with { Sort = field }));
        ToggleDirectionCommand = new RelayCommand(() =>
            apply(Settings with { Ascending = !Settings.Ascending }));
    }

    public LibraryViewSettings Settings { get; private set; } = LibraryViewSettings.Default(LibraryItemKind.Video);

    public IRelayCommand<LibrarySortField> SetSortCommand { get; }

    public IRelayCommand ToggleDirectionCommand { get; }

    public bool IsTitle => Settings.Sort == LibrarySortField.Title;
    public bool IsDate => Settings.Sort == LibrarySortField.DateAdded;
    public bool IsDuration => Settings.Sort == LibrarySortField.Duration;
    public bool IsType => Settings.Sort == LibrarySortField.FileType;
    public bool IsSize => Settings.Sort == LibrarySortField.FileSize;

    public string ShortTitle => Localized(Settings.Sort switch
    {
        LibrarySortField.DateAdded => nameof(Resources.Props_Date),
        LibrarySortField.Duration => nameof(Resources.Common_ColumnLength),
        LibrarySortField.FileType => nameof(Resources.Props_Type),
        LibrarySortField.FileSize => nameof(Resources.Props_Size),
        _ => nameof(Resources.Shell_SortTitle),
    });

    public string DirectionText => Localized(Settings.Ascending
        ? nameof(Resources.Shell_SortAscending) : nameof(Resources.Shell_SortDescending));

    public void ApplySettings(LibraryViewSettings settings)
    {
        if (Settings == settings)
            return;

        Settings = settings;
        OnPropertyChanged((string?)null);
    }
}