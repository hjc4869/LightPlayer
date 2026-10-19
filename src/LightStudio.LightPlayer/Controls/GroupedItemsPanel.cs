using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.Controls;

public sealed class GroupedItemsPanel : VirtualizingPanel
{
    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<GroupedItemsPanel, double>(nameof(MinItemWidth), 130);
    public static readonly StyledProperty<double> MinItemHeightProperty =
        AvaloniaProperty.Register<GroupedItemsPanel, double>(nameof(MinItemHeight), 160);
    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<GroupedItemsPanel, double>(nameof(ColumnSpacing), 6);
    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<GroupedItemsPanel, double>(nameof(RowSpacing), 2);
    public static readonly StyledProperty<double> HeaderHeightProperty =
        AvaloniaProperty.Register<GroupedItemsPanel, double>(nameof(HeaderHeight), 28);
    public static readonly StyledProperty<bool> IsListProperty =
        AvaloniaProperty.Register<GroupedItemsPanel, bool>(nameof(IsList));
    public static readonly StyledProperty<bool> FoldersAsListProperty =
        AvaloniaProperty.Register<GroupedItemsPanel, bool>(nameof(FoldersAsList));
    public static readonly StyledProperty<Thickness> ContentInsetProperty =
        AvaloniaProperty.Register<GroupedItemsPanel, Thickness>(nameof(ContentInset));

    private readonly Dictionary<int, RealizedItem> realized = [];
    private readonly Dictionary<Control, int> indices = [];
    private readonly Dictionary<(object Key, Type Type, bool Folder), Stack<Control>> pool = [];
    private readonly HashSet<Control> anchors = [];
    private IScrollAnchorProvider? anchorProvider;
    private GroupedItemsModel? source;
    private IDataTemplate? currentTemplate;
    private LayoutKey? layoutKey;
    private double[] groupTops = [];
    private double itemWidth;
    private double itemHeight;
    private double folderItemHeight = 68;
    private double extentHeight;
    private double width;
    private int columns = 1;
    private Rect viewport;
    private int realizedFirst = -1;
    private int realizedLast = -1;
    private double? pendingScrollOffset;
    private int resizeAnchorIndex = -1;
    private double resizeAnchorOffset;
    private double resizeScrollOrigin;

    static GroupedItemsPanel() => AffectsMeasure<GroupedItemsPanel>(MinItemWidthProperty,
        MinItemHeightProperty, ColumnSpacingProperty, RowSpacingProperty, HeaderHeightProperty, IsListProperty,
        FoldersAsListProperty, ContentInsetProperty);

    public GroupedItemsPanel() => EffectiveViewportChanged += OnViewportChanged;

    public double MinItemWidth { get => GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }
    public double MinItemHeight { get => GetValue(MinItemHeightProperty); set => SetValue(MinItemHeightProperty, value); }
    public double ColumnSpacing { get => GetValue(ColumnSpacingProperty); set => SetValue(ColumnSpacingProperty, value); }
    public double RowSpacing { get => GetValue(RowSpacingProperty); set => SetValue(RowSpacingProperty, value); }
    public double HeaderHeight { get => GetValue(HeaderHeightProperty); set => SetValue(HeaderHeightProperty, value); }
    public bool IsList { get => GetValue(IsListProperty); set => SetValue(IsListProperty, value); }
    public bool FoldersAsList { get => GetValue(FoldersAsListProperty); set => SetValue(FoldersAsListProperty, value); }
    public Thickness ContentInset { get => GetValue(ContentInsetProperty); set => SetValue(ContentInsetProperty, value); }

    public Rect GetItemBounds(int index)
    {
        if (source is null || (uint)index >= (uint)source.Count)
            return default;
        var group = source.Groups[source.GroupFromIndex(index)];
        if (layoutKey is null || group.GroupIndex >= groupTops.Length)
            return default;
        var top = groupTops[group.GroupIndex];
        var row = GetRowLayout(group);
        if (index == group.Index)
            return new Rect(row.Left, top, row.Columns * row.Width + (row.Columns - 1) * layoutKey!.Value.ColumnSpacing, row.HeaderHeight);
        var local = index - group.Index - 1;
        return new Rect(row.Left + local % row.Columns * (row.Width + layoutKey!.Value.ColumnSpacing),
            top + row.HeaderHeight + local / row.Columns * (row.Height + row.Spacing), row.Width, row.Height);
    }

    private (int Columns, double Width, double Height, double Spacing, double Left, double HeaderHeight) GetRowLayout(LibraryGroupHeader group)
    {
        var current = layoutKey!.Value;
        return current.FoldersAsList && group.IsFolder
            ? (1, Math.Max(1, current.Width - 16), folderItemHeight, 0, 8, 0)
            : (columns, itemWidth, itemHeight, current.RowSpacing, current.ContentInset.Left, current.HeaderHeight);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var templateChanged = !ReferenceEquals(currentTemplate, ItemsControl?.ItemTemplate);
        if (templateChanged)
        {
            currentTemplate = ItemsControl?.ItemTemplate;
            ClearContainers();
        }
        if (ItemsControl?.ItemsSource is not GroupedItemsModel model || model.Count == 0)
        {
            RecycleOutside(-1, -1);
            return default;
        }
        var availableWidth = double.IsFinite(availableSize.Width) ? Math.Max(1, availableSize.Width) : Math.Max(1, MinItemWidth);
        var nextKey = new LayoutKey(model, availableWidth, MinItemWidth, MinItemHeight, ColumnSpacing, RowSpacing, HeaderHeight,
            IsList, FoldersAsList, ContentInset, TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);
        var changed = layoutKey != nextKey || templateChanged;
        var scrollViewer = this.FindAncestorOfType<ScrollViewer>();
        var preservePosition = changed && layoutKey is { } previous && ReferenceEquals(previous.Source, model) &&
            viewport.Height > 0 && groupTops.Length > 0 && scrollViewer is not null;
        if (preservePosition && resizeAnchorIndex < 0)
        {
            resizeAnchorIndex = IndexAtOffset(GroupAtOffset(viewport.Top), viewport.Top, false);
            resizeAnchorOffset = viewport.Top - GetItemBounds(resizeAnchorIndex).Top;
            resizeScrollOrigin = scrollViewer!.Offset.Y - viewport.Top;
        }
        source = model;
        if (changed)
        {
            layoutKey = nextKey;
            width = Math.Max(1, availableWidth - ContentInset.Left - ContentInset.Right);
            columns = IsList ? 1 : Math.Max(1, (int)((width + ColumnSpacing) / (Math.Max(1, MinItemWidth) + ColumnSpacing)));
            itemWidth = (width - (columns - 1) * ColumnSpacing) / columns;
            itemHeight = IsList ? Math.Max(1, MinItemHeight) : Math.Max(1, MinItemHeight) * itemWidth / Math.Max(1, MinItemWidth);
            folderItemHeight = 68;
            if (FoldersAsList && model.Groups.FirstOrDefault(group => group.IsFolder && group.Count > 0) is { } folders &&
                model[folders.Index + 1] is VideoTileModel { IsFolder: true })
            {
                var row = Realize(folders.Index + 1);
                row.Measure(new Size(Math.Max(1, availableWidth - 16), double.PositiveInfinity));
                if (double.IsFinite(row.DesiredSize.Height) && row.DesiredSize.Height > 0)
                    folderItemHeight = row.DesiredSize.Height;
            }
            groupTops = new double[model.Groups.Count];
            extentHeight = 0;
            foreach (var group in model.Groups)
            {
                var row = GetRowLayout(group);
                var folderList = FoldersAsList && group.IsFolder;
                if (folderList)
                    extentHeight += 8;
                else if (group.GroupIndex == 0 || FoldersAsList && model.Groups[group.GroupIndex - 1].IsFolder)
                    extentHeight += ContentInset.Top;
                groupTops[group.GroupIndex] = extentHeight;
                var rows = (group.Count + row.Columns - 1) / row.Columns;
                extentHeight += row.HeaderHeight + rows * row.Height + Math.Max(0, rows - 1) * row.Spacing + (folderList ? 0 : 6);
            }
            extentHeight += ContentInset.Bottom;
        }

        var visible = viewport.Height > 0 ? viewport : new Rect(0, 0, width, 400);
        if (preservePosition)
        {
            var top = GetItemBounds(resizeAnchorIndex).Top + resizeAnchorOffset;
            visible = new Rect(visible.X, top, visible.Width, visible.Height);
            pendingScrollOffset = Math.Max(0, top + resizeScrollOrigin);
            LayoutUpdated -= RestoreScrollPosition;
            LayoutUpdated += RestoreScrollPosition;
        }
        GetVisibleRange(visible, out var first, out var last);
        RecycleOutside(first, last);
        for (var index = first; index <= last; index++)
        {
            var control = Realize(index);
            control.IsVisible = GetItemBounds(index).Height > 0;
            if (changed || !control.IsMeasureValid)
                control.Measure(GetItemBounds(index).Size);
        }
        realizedFirst = first;
        realizedLast = last;
        model.EnsureRange(first, last);
        return new Size(availableWidth, extentHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var pair in realized)
        {
            var bounds = GetItemBounds(pair.Key);
            var control = pair.Value.Control;
            control.Arrange(bounds);
            var firstColumn = bounds.X == GetRowLayout(source!.Groups[source.GroupFromIndex(pair.Key)]).Left;
            if (firstColumn && anchorProvider is not null && anchors.Add(control))
                anchorProvider.RegisterAnchorCandidate(control);
            else if (!firstColumn && anchors.Remove(control))
                anchorProvider?.UnregisterAnchorCandidate(control);
        }
        return finalSize;
    }

    private void GetVisibleRange(Rect visible, out int first, out int last)
    {
        var top = Math.Max(0, visible.Top - itemHeight - RowSpacing);
        var bottom = Math.Min(extentHeight, visible.Bottom + itemHeight + RowSpacing);
        var firstGroup = GroupAtOffset(top);
        var lastGroup = GroupAtOffset(Math.Max(top, bottom));
        first = IndexAtOffset(firstGroup, top, false);
        last = Math.Max(first, IndexAtOffset(lastGroup, bottom, true));
    }

    private int GroupAtOffset(double offset)
    {
        var found = Array.BinarySearch(groupTops, offset);
        return found >= 0 ? found : Math.Clamp(~found - 1, 0, groupTops.Length - 1);
    }

    private int IndexAtOffset(int groupIndex, double offset, bool lastColumn)
    {
        var group = source!.Groups[groupIndex];
        var layout = GetRowLayout(group);
        var localTop = offset - groupTops[groupIndex] - layout.HeaderHeight;
        if (localTop < 0 || group.Count == 0)
            return group.Index;
        var row = Math.Max(0, (int)Math.Floor(localTop / (layout.Height + layout.Spacing)));
        return group.Index + 1 + Math.Min(group.Count - 1, row * layout.Columns + (lastColumn ? layout.Columns - 1 : 0));
    }

    private Control Realize(int index)
    {
        if (realized.TryGetValue(index, out var existing))
            return existing.Control;
        var item = Items[index];
        var generator = ItemContainerGenerator!;
        var needsContainer = generator.NeedsContainer(item, index, out var recycleKey);
        var poolKey = recycleKey is null ? ((object Key, Type Type, bool Folder)?)null
            : (recycleKey, item!.GetType(), FoldersAsList && item is VideoTileModel { IsFolder: true });
        Control container;
        if (poolKey is { } key && pool.TryGetValue(key, out var available) && available.Count > 0)
        {
            container = available.Pop();
            container.IsVisible = true;
            RecycledContainer.Prepare(generator, container, item, index);
        }
        else
        {
            container = needsContainer ? generator.CreateContainer(item, index, recycleKey) : (Control)item!;
            generator.PrepareItemContainer(container, item, index);
            AddInternalChild(container);
        }
        generator.ItemContainerPrepared(container, item, index);
        realized.Add(index, new RealizedItem(container, item, poolKey));
        indices.Add(container, index);
        return container;
    }

    private void RecycleOutside(int first, int last)
    {
        foreach (var index in realized.Keys.Where(index => index < first || index > last).ToArray())
            Recycle(index);
    }

    private void Recycle(int index)
    {
        var entry = realized[index];
        realized.Remove(index);
        indices.Remove(entry.Control);
        if (anchors.Remove(entry.Control))
            anchorProvider?.UnregisterAnchorCandidate(entry.Control);
        if (entry.PoolKey is { } key)
        {
            entry.Control.IsVisible = false;
            if (!pool.TryGetValue(key, out var available))
                pool.Add(key, available = new Stack<Control>());
            available.Push(entry.Control);
        }
        else
        {
            ItemContainerGenerator?.ClearItemContainer(entry.Control);
            RemoveInternalChild(entry.Control);
        }
    }

    protected override void OnItemsChanged(IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs args)
    {
        var nextSource = ItemsControl?.ItemsSource as GroupedItemsModel;
        if (!ReferenceEquals(source, nextSource))
        {
            ClearContainers();
            source = nextSource;
            layoutKey = null;
            groupTops = [];
            pendingScrollOffset = null;
            resizeAnchorIndex = -1;
            LayoutUpdated -= RestoreScrollPosition;
        }
        foreach (var pair in realized.ToArray())
        {
            if (pair.Key >= items.Count || !ReferenceEquals(pair.Value.Item, items[pair.Key]))
                Recycle(pair.Key);
        }
        InvalidateMeasure();
    }

    protected override void OnItemsControlChanged(ItemsControl? oldValue)
    {
        base.OnItemsControlChanged(oldValue);
        ClearContainers();
        layoutKey = null;
        source = null;
    }

    private void ClearContainers()
    {
        RecycleOutside(-1, -1);
        foreach (var container in pool.Values.SelectMany(available => available))
        {
            ItemContainerGenerator?.ClearItemContainer(container);
            RemoveInternalChild(container);
        }
        pool.Clear();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs args)
    {
        base.OnAttachedToVisualTree(args);
        anchorProvider = this.FindAncestorOfType<IScrollAnchorProvider>();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs args)
    {
        LayoutUpdated -= RestoreScrollPosition;
        pendingScrollOffset = null;
        resizeAnchorIndex = -1;
        foreach (var control in anchors)
            anchorProvider?.UnregisterAnchorCandidate(control);
        anchors.Clear();
        anchorProvider = null;
        viewport = default;
        base.OnDetachedFromVisualTree(args);
    }

    private void RestoreScrollPosition(object? sender, EventArgs args)
    {
        if (pendingScrollOffset is not { } target || this.FindAncestorOfType<ScrollViewer>() is not { } scrollViewer ||
            !IsArrangeValid || scrollViewer.Extent.Height < extentHeight - 1)
            return;
        LayoutUpdated -= RestoreScrollPosition;
        pendingScrollOffset = null;
        resizeAnchorIndex = -1;
        scrollViewer.Offset = new Vector(scrollViewer.Offset.X, target);
    }

    private void OnViewportChanged(object? sender, EffectiveViewportChangedEventArgs args)
    {
        if (!IsEffectivelyVisible || args.EffectiveViewport.Height <= 0 || args.EffectiveViewport.Width <= 0)
            return;
        if (pendingScrollOffset is not null)
            return;
        viewport = args.EffectiveViewport;
        if (source is null || groupTops.Length == 0)
        {
            InvalidateMeasure();
            return;
        }
        GetVisibleRange(viewport, out var first, out var last);
        if (first != realizedFirst || last != realizedLast)
            InvalidateMeasure();
    }

    protected override Control? ContainerFromIndex(int index) => realized.GetValueOrDefault(index)?.Control;
    protected override int IndexFromContainer(Control container) => indices.GetValueOrDefault(container, -1);
    protected override IEnumerable<Control>? GetRealizedContainers() => realized.Values.Select(item => item.Control).ToArray();

    protected override Control? ScrollIntoView(int index)
    {
        if ((uint)index >= (uint)Items.Count)
            return null;
        this.BringIntoView(GetItemBounds(index));
        UpdateLayout();
        return ContainerFromIndex(index);
    }

    protected override IInputElement? GetControl(NavigationDirection direction, IInputElement? from, bool wrap)
    {
        var container = from as Control;
        while (container is not null && !indices.ContainsKey(container))
            container = container.GetVisualParent() as Control;
        var index = container is not null ? IndexFromContainer(container) : -1;
        var target = direction switch
        {
            NavigationDirection.First => 0,
            NavigationDirection.Last => Items.Count - 1,
            NavigationDirection.Next or NavigationDirection.Right => index + 1,
            NavigationDirection.Previous or NavigationDirection.Left => index - 1,
            NavigationDirection.Up => index - columns,
            NavigationDirection.Down => index + columns,
            _ => -1,
        };
        return ScrollIntoView(target);
    }

    private sealed record RealizedItem(Control Control, object? Item, (object Key, Type Type, bool Folder)? PoolKey);
    private readonly record struct LayoutKey(GroupedItemsModel Source, double Width, double ItemWidth,
        double ItemHeight, double ColumnSpacing, double RowSpacing, double HeaderHeight, bool IsList,
        bool FoldersAsList, Thickness ContentInset, double RenderScaling);
}
