using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.Controls;

public partial class GroupedItemsView : UserControl
{
    public static readonly StyledProperty<GroupedItemsModel?> ModelProperty =
        AvaloniaProperty.Register<GroupedItemsView, GroupedItemsModel?>(nameof(Model));
    public static readonly StyledProperty<IDataTemplate?> ItemTemplateProperty =
        AvaloniaProperty.Register<GroupedItemsView, IDataTemplate?>(nameof(ItemTemplate));
    public static readonly StyledProperty<IDataTemplate?> FolderItemTemplateProperty =
        AvaloniaProperty.Register<GroupedItemsView, IDataTemplate?>(nameof(FolderItemTemplate));
    public static readonly StyledProperty<bool> FoldersAsListProperty =
        AvaloniaProperty.Register<GroupedItemsView, bool>(nameof(FoldersAsList));
    public static readonly StyledProperty<Thickness> ContentInsetProperty =
        AvaloniaProperty.Register<GroupedItemsView, Thickness>(nameof(ContentInset), new Thickness(32));
    public static readonly StyledProperty<Thickness> HeaderPaddingProperty =
        AvaloniaProperty.Register<GroupedItemsView, Thickness>(nameof(HeaderPadding));
    public static readonly StyledProperty<string?> HeaderAlignmentTargetProperty =
        AvaloniaProperty.Register<GroupedItemsView, string?>(nameof(HeaderAlignmentTarget));
    public static readonly DirectProperty<GroupedItemsView, Thickness> ActualHeaderPaddingProperty =
        AvaloniaProperty.RegisterDirect<GroupedItemsView, Thickness>(nameof(ActualHeaderPadding), view => view.ActualHeaderPadding);
    public static readonly DirectProperty<GroupedItemsView, Thickness> PickerHeaderInsetProperty =
        AvaloniaProperty.RegisterDirect<GroupedItemsView, Thickness>(nameof(PickerHeaderInset), view => view.PickerHeaderInset);
    public static readonly DirectProperty<GroupedItemsView, Thickness> PickerContentInsetProperty =
        AvaloniaProperty.RegisterDirect<GroupedItemsView, Thickness>(nameof(PickerContentInset), view => view.PickerContentInset);
    public static readonly StyledProperty<double> LeadingHeightProperty =
        AvaloniaProperty.Register<GroupedItemsView, double>(nameof(LeadingHeight));
    public static readonly StyledProperty<Thickness> LeadingMarginProperty =
        AvaloniaProperty.Register<GroupedItemsView, Thickness>(nameof(LeadingMargin));
    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<GroupedItemsView, double>(nameof(MinItemWidth), 130);
    public static readonly StyledProperty<double> MinItemHeightProperty =
        AvaloniaProperty.Register<GroupedItemsView, double>(nameof(MinItemHeight), 160);
    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<GroupedItemsView, double>(nameof(ColumnSpacing), 6);
    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<GroupedItemsView, double>(nameof(RowSpacing), 2);
    public static readonly StyledProperty<bool> IsListProperty =
        AvaloniaProperty.Register<GroupedItemsView, bool>(nameof(IsList));
    public static readonly StyledProperty<bool> SelectOnTapProperty =
        AvaloniaProperty.Register<GroupedItemsView, bool>(nameof(SelectOnTap));
    public static readonly StyledProperty<SelectionItemModel?> SelectedItemProperty =
        AvaloniaProperty.Register<GroupedItemsView, SelectionItemModel?>(nameof(SelectedItem),
            defaultBindingMode: BindingMode.TwoWay);

    private GroupedItemsModel? observed;
    private Vector savedOffset;
    private bool initialized;
    private bool attached;
    private Thickness pickerHeaderInset;
    private Thickness pickerContentInset;
    private Thickness actualHeaderPadding;

    public GroupedItemsView()
    {
        InitializeComponent();
        initialized = true;
        ItemsHost.AddHandler(TappedEvent, OnItemTapped);
        ItemsHost.LayoutUpdated += (_, _) =>
        {
            UpdateSelection();
            UpdateHeaderPadding();
        };
        GroupPicker.LayoutUpdated += (_, _) => UpdatePickerInsets();
        GroupPickerScroller.PropertyChanged += (_, change) =>
        {
            if (change.Property == ScrollViewer.ViewportProperty || change.Property == BoundsProperty)
                UpdatePickerInsets();
        };
        UpdatePickerInsets();
    }

    public GroupedItemsModel? Model { get => GetValue(ModelProperty); set => SetValue(ModelProperty, value); }
    public IDataTemplate? ItemTemplate { get => GetValue(ItemTemplateProperty); set => SetValue(ItemTemplateProperty, value); }
    public IDataTemplate? FolderItemTemplate { get => GetValue(FolderItemTemplateProperty); set => SetValue(FolderItemTemplateProperty, value); }
    public bool FoldersAsList { get => GetValue(FoldersAsListProperty); set => SetValue(FoldersAsListProperty, value); }
    public Thickness ContentInset { get => GetValue(ContentInsetProperty); set => SetValue(ContentInsetProperty, value); }
    public Thickness HeaderPadding { get => GetValue(HeaderPaddingProperty); set => SetValue(HeaderPaddingProperty, value); }
    public string? HeaderAlignmentTarget { get => GetValue(HeaderAlignmentTargetProperty); set => SetValue(HeaderAlignmentTargetProperty, value); }
    public Thickness ActualHeaderPadding => actualHeaderPadding;
    public Thickness PickerHeaderInset => pickerHeaderInset;
    public Thickness PickerContentInset => pickerContentInset;
    public double LeadingHeight { get => GetValue(LeadingHeightProperty); set => SetValue(LeadingHeightProperty, value); }
    public Thickness LeadingMargin { get => GetValue(LeadingMarginProperty); set => SetValue(LeadingMarginProperty, value); }
    public double MinItemWidth { get => GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }
    public double MinItemHeight { get => GetValue(MinItemHeightProperty); set => SetValue(MinItemHeightProperty, value); }
    public double ColumnSpacing { get => GetValue(ColumnSpacingProperty); set => SetValue(ColumnSpacingProperty, value); }
    public double RowSpacing { get => GetValue(RowSpacingProperty); set => SetValue(RowSpacingProperty, value); }
    public bool IsList { get => GetValue(IsListProperty); set => SetValue(IsListProperty, value); }
    public bool SelectOnTap { get => GetValue(SelectOnTapProperty); set => SetValue(SelectOnTapProperty, value); }
    public SelectionItemModel? SelectedItem { get => GetValue(SelectedItemProperty); set => SetValue(SelectedItemProperty, value); }
    public ScrollViewer ScrollViewer => ItemsScroller;
    public ItemsControl ItemsControl => ItemsHost;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (!initialized)
            return;
        if (change.Property == ItemTemplateProperty || change.Property == FolderItemTemplateProperty || change.Property == FoldersAsListProperty)
        {
            var template = ItemTemplate;
            var folderTemplate = FoldersAsList ? FolderItemTemplate : null;
            ItemsHost.ItemTemplate = new FuncDataTemplate<object?>((item, _) =>
                item is VideoTileModel { IsFolder: true } && folderTemplate is not null
                    ? folderTemplate.Build(item)
                    : item is SelectionItemModel
                    ? template?.Build(item)
                    : ItemsHost.DataTemplates.FirstOrDefault(candidate => candidate.Match(item))?.Build(item),
                supportsRecycling: true);
        }
        else if (change.Property == ModelProperty)
            ObserveModel();
        else if (change.Property == ContentInsetProperty || change.Property == HeaderPaddingProperty || change.Property == HeaderAlignmentTargetProperty)
            UpdateHeaderPadding();
        else if (change.Property == SelectedItemProperty)
            UpdateSelection();
    }

    private void UpdateHeaderPadding()
    {
        var padding = HeaderPadding;
        if (HeaderAlignmentTarget is { Length: > 0 } name && IsEffectivelyVisible)
        {
            foreach (var container in ItemsHost.GetRealizedContainers())
            {
                if (!container.IsVisible || ItemsHost.ItemFromContainer(container) is not SelectionItemModel || container.Bounds.Width <= 0)
                    continue;
                var target = container.GetVisualDescendants().OfType<Control>().FirstOrDefault(control => control.Name == name);
                if (target is null || target.Bounds.Width <= 0 ||
                    target.TranslatePoint(default, container) is not { } left ||
                    target.TranslatePoint(new Point(target.Bounds.Width, 0), container) is not { } right)
                    continue;
                padding = new Thickness(Math.Max(0, left.X), 0, Math.Max(0, container.Bounds.Width - right.X), 0);
                break;
            }
        }
        SetAndRaise(ActualHeaderPaddingProperty, ref actualHeaderPadding, padding);
        UpdatePickerInsets();
    }

    private void UpdatePickerInsets()
    {
        var left = ContentInset.Left + ActualHeaderPadding.Left;
        var right = ContentInset.Right + ActualHeaderPadding.Right;
        var scrollbar = GroupPickerScroller.Viewport.Width > 0
            ? Math.Max(0, GroupPickerScroller.Bounds.Width - GroupPickerScroller.Viewport.Width) : 0;
        var gridWidth = GroupPicker.ItemsPanelRoot?.DesiredSize.Width ?? 0;
        var remainder = gridWidth > 0 ? Math.Max(0, GroupPicker.Bounds.Width - gridWidth) : 0;
        SetAndRaise(PickerHeaderInsetProperty, ref pickerHeaderInset, new Thickness(left, ContentInset.Top,
            Math.Max(0, right + scrollbar + remainder - 8), 4));
        SetAndRaise(PickerContentInsetProperty, ref pickerContentInset, new Thickness(left, 4, right, ContentInset.Bottom));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs args)
    {
        base.OnAttachedToVisualTree(args);
        attached = true;
        ObserveModel();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs args)
    {
        attached = false;
        ObserveModel();
        base.OnDetachedFromVisualTree(args);
    }

    private void ObserveModel()
    {
        if (observed is not null)
        {
            observed.JumpRequested -= JumpToIndex;
            observed.PropertyChanged -= OnModelChanged;
        }
        observed = attached ? Model : null;
        if (observed is not null)
        {
            observed.JumpRequested += JumpToIndex;
            observed.PropertyChanged += OnModelChanged;
        }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(GroupedItemsModel.IsPickerOpen))
            return;
        if (Model?.IsPickerOpen == true)
        {
            savedOffset = ItemsScroller.Offset;
            Dispatcher.UIThread.Post(() => ClosePickerButton.Focus(), DispatcherPriority.Loaded);
        }
        else
        {
            Dispatcher.UIThread.Post(() => ItemsScroller.Offset = savedOffset, DispatcherPriority.Loaded);
        }
    }

    private void JumpToIndex(int index) => Dispatcher.UIThread.Post(() =>
    {
        ItemsHost.UpdateLayout();
        if (ItemsHost.ItemsPanelRoot is GroupedItemsPanel panel)
        {
            ItemsScroller.Offset = new Vector(0, panel.GetItemBounds(index).Top + LeadingHeight + LeadingMargin.Top + LeadingMargin.Bottom);
            ItemsHost.UpdateLayout();
            ItemsHost.ContainerFromIndex(index)?.FindDescendantOfType<Button>()?.Focus();
        }
    }, DispatcherPriority.Loaded);

    private void OnItemTapped(object? sender, TappedEventArgs args)
    {
        if (SelectOnTap && (args.Source as Control)?.FindAncestorOfType<SelectionItem>(includeSelf: true)
                ?.DataContext is SelectionItemModel { IsSelectionMode: false } item)
        {
            SetCurrentValue(SelectedItemProperty, item);
            args.Handled = true;
        }
    }

    private void UpdateSelection()
    {
        if (!SelectOnTap || !IsEffectivelyVisible)
            return;
        foreach (var container in ItemsHost.GetRealizedContainers())
        {
            var item = ItemsHost.ItemFromContainer(container) as SelectionItemModel;
            container.Classes.Set("groupRow", item is { IsSelectionMode: false });
            container.Classes.Set("groupSelected", item is not null && ReferenceEquals(item, SelectedItem));
        }
    }

    protected override void OnKeyDown(KeyEventArgs args)
    {
        if (args.Key == Key.Escape && Model?.IsPickerOpen == true)
        {
            Model.IsPickerOpen = false;
            args.Handled = true;
        }
        else if (SelectOnTap && args.Key == Key.Enter &&
                 (args.Source as Control)?.DataContext is SelectionItemModel { IsSelectionMode: false } item)
        {
            SetCurrentValue(SelectedItemProperty, item);
            args.Handled = true;
        }
        base.OnKeyDown(args);
    }
}
