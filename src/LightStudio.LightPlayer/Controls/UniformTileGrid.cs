using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace LightStudio.LightPlayer.Controls;

/// <summary>
/// Virtualizing items panel that lays tiles out in a uniform grid, the panel the library grids use
/// as their <c>ItemsControl.ItemsPanel</c>.
///
/// It reproduces the geometry the retired <c>ItemsRepeater</c> + <c>UniformGridLayout</c> pair
/// produced with <c>ItemsStretch="Uniform"</c>: the column count comes from
/// <see cref="MinItemWidth"/> plus <see cref="MinColumnSpacing"/>, the leftover width of a row is
/// shared out over its items, and the extra height each item gets keeps the
/// <see cref="MinItemWidth"/> : <see cref="MinItemHeight"/> aspect ratio. Every cell therefore has
/// the same size, which is what makes the virtualization here cheap: the realized window is a plain
/// arithmetic slice of the item list rather than a running measurement of what has been laid out so
/// far.
///
/// Only the rows intersecting the viewport are realized. The viewport is taken from
/// <see cref="Layoutable.EffectiveViewportChanged"/>, so the panel does not need to be the direct
/// child of the <see cref="ScrollViewer"/> -- the search and home grids sit inside a stack of
/// sections and still virtualize.
/// </summary>
public class UniformTileGrid : VirtualizingPanel
{
    /// <summary>Sentinel meaning "no viewport reported yet"; matches Avalonia's own panels.</summary>
    private static readonly Rect UnknownViewport = new(double.PositiveInfinity, double.PositiveInfinity, 0, 0);

    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<UniformTileGrid, double>(nameof(MinItemWidth), 1d);

    public static readonly StyledProperty<double> MinItemHeightProperty =
        AvaloniaProperty.Register<UniformTileGrid, double>(nameof(MinItemHeight), 1d);

    public static readonly StyledProperty<double> MinColumnSpacingProperty =
        AvaloniaProperty.Register<UniformTileGrid, double>(nameof(MinColumnSpacing));

    public static readonly StyledProperty<double> MinRowSpacingProperty =
        AvaloniaProperty.Register<UniformTileGrid, double>(nameof(MinRowSpacing));

    public static readonly StyledProperty<int> TotalCountProperty =
        AvaloniaProperty.Register<UniformTileGrid, int>(nameof(TotalCount));

    /// <summary>
    /// Rows prepared above and below the viewport in bounded batches while scrolling.
    /// </summary>
    private const int BufferRows = 1;
    private const int MaxBufferedItemsPerPass = 2;
    private static readonly TimeSpan BufferPreparationBudget = TimeSpan.FromMilliseconds(2);

    private readonly Dictionary<int, RealizedItem> realized = new();
    private readonly Dictionary<Control, int> indexByContainer = new();
    private readonly Dictionary<object, Stack<Control>> recyclePool = new();
    private readonly List<RealizedItem> pendingRecycle = new();
    private readonly List<int> scratch = new();
    private readonly HashSet<Control> anchorCandidates = new();

    private IScrollAnchorProvider? scrollAnchorProvider;
    private Rect viewport = UnknownViewport;
    private double itemWidth = 1d;
    private double itemHeight = 1d;
    private int columns = 1;
    private int arrangedColumns;
    private double arrangedRowPitch;
    private bool scrollingForward = true;
    private bool prepareBuffer;
    private int realizedFirstRow = -1;
    private int realizedLastRow = -1;
    private int realizedFirstIndex = -1;
    private int realizedLastIndex = -1;

    static UniformTileGrid()
    {
        AffectsMeasure<UniformTileGrid>(
            MinItemWidthProperty,
            MinItemHeightProperty,
            MinColumnSpacingProperty,
            MinRowSpacingProperty,
            TotalCountProperty);
    }

    public UniformTileGrid()
    {
        EffectiveViewportChanged += OnEffectiveViewportChanged;
    }

    /// <summary>Width a cell shrinks to before the row drops a column.</summary>
    public double MinItemWidth
    {
        get => GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    /// <summary>Cell height at <see cref="MinItemWidth"/>; it grows with the cell width.</summary>
    public double MinItemHeight
    {
        get => GetValue(MinItemHeightProperty);
        set => SetValue(MinItemHeightProperty, value);
    }

    public double MinColumnSpacing
    {
        get => GetValue(MinColumnSpacingProperty);
        set => SetValue(MinColumnSpacingProperty, value);
    }

    public double MinRowSpacing
    {
        get => GetValue(MinRowSpacingProperty);
        set => SetValue(MinRowSpacingProperty, value);
    }

    /// <summary>Total items in an incremental source, including those not loaded yet.</summary>
    public int TotalCount
    {
        get => GetValue(TotalCountProperty);
        set => SetValue(TotalCountProperty, value);
    }

    /// <summary>Number of items in the current viewport plus its target row buffer.</summary>
    internal int RealizationWindowSize { get; private set; } = 1;

    /// <summary>Size of every cell in the most recent measure.</summary>
    internal Size ItemSize => new(itemWidth, itemHeight);

    protected override Size MeasureOverride(Size availableSize)
    {
        var items = Items;
        var count = items.Count;

        if (count == 0)
        {
            prepareBuffer = false;
            UnrealizeAllExcept(-1, -1);
            FlushPendingRecycle();
            columns = 1;
            realizedFirstRow = -1;
            realizedLastRow = -1;
            realizedFirstIndex = -1;
            realizedLastIndex = -1;
            RealizationWindowSize = 1;
            return default;
        }

        var previousItemWidth = itemWidth;
        var previousItemHeight = itemHeight;
        if (availableSize.Width > 0 || realizedFirstIndex < 0)
        {
            MeasureCells(availableSize.Width, count);
        }
        else
        {
            prepareBuffer = false;
        }

        var rows = (count + columns - 1) / columns;
        var virtualRows = (Math.Max(count, TotalCount) + columns - 1) / columns;
        var extent = new Size(
            columns * itemWidth + Math.Max(0, columns - 1) * MinColumnSpacing,
            virtualRows * itemHeight + Math.Max(0, virtualRows - 1) * MinRowSpacing);

        var layoutViewport = GetLayoutViewport();
        GetVisibleRows(layoutViewport, rows, out var firstRow, out var lastRow);
        var targetFirst = firstRow * columns;
        var targetLast = Math.Min(count - 1, ((lastRow + 1) * columns) - 1);
        RealizationWindowSize = targetLast - targetFirst + 1;
        GetVisibleRows(layoutViewport, rows, out var visibleFirstRow, out var visibleLastRow, 0);
        var first = visibleFirstRow * columns;
        var last = Math.Min(count - 1, ((visibleLastRow + 1) * columns) - 1);
        var previousFirst = realizedFirstIndex;
        var previousLast = realizedLastIndex;

        if (previousFirst >= 0 && previousFirst <= targetLast && previousLast >= targetFirst)
        {
            first = Math.Min(first, Math.Max(targetFirst, previousFirst));
            last = Math.Max(last, Math.Min(targetLast, previousLast));
        }

        UnrealizeAllExcept(first, last);

        var cell = new Size(itemWidth, itemHeight);

        if (previousFirst < 0)
        {
            RealizeRange(first, last, items, cell);
        }
        else
        {
            RealizeRange(first, Math.Min(last, previousFirst - 1), items, cell);
            RealizeRange(Math.Max(first, previousLast + 1), last, items, cell);
        }

        if (prepareBuffer)
        {
            prepareBuffer = false;
            var start = Stopwatch.GetTimestamp();

            for (var prepared = 0; prepared < MaxBufferedItemsPerPass; ++prepared)
            {
                if (last < targetLast && (scrollingForward || first == targetFirst))
                {
                    Realize(++last, items).Measure(cell);
                }
                else if (first > targetFirst)
                {
                    Realize(--first, items).Measure(cell);
                }
                else
                {
                    break;
                }

                if (Stopwatch.GetElapsedTime(start) >= BufferPreparationBudget)
                {
                    break;
                }
            }
        }

        FlushPendingRecycle();

        var cellSizeChanged = itemWidth != previousItemWidth || itemHeight != previousItemHeight;

        foreach (var pair in realized)
        {
            if (cellSizeChanged || !pair.Value.Control.IsMeasureValid)
            {
                pair.Value.Control.Measure(cell);
            }
        }

        realizedFirstRow = (first + columns - 1) / columns;
        realizedLastRow = last == count - 1 ? last / columns : ((last + 1) / columns) - 1;
        realizedFirstIndex = first;
        realizedLastIndex = last;

        return extent;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columnPitch = itemWidth + MinColumnSpacing;
        var rowPitch = itemHeight + MinRowSpacing;

        foreach (var pair in realized)
        {
            var row = pair.Key / columns;
            var column = pair.Key - (row * columns);
            var control = pair.Value.Control;

            control.Arrange(new Rect(
                column * columnPitch,
                row * rowPitch,
                itemWidth,
                itemHeight));

            if (column == 0 && scrollAnchorProvider is not null && anchorCandidates.Add(control))
            {
                scrollAnchorProvider.RegisterAnchorCandidate(control);
            }
        }

        if (columns != arrangedColumns)
        {
            LayoutUpdated -= OnAnchorCandidatesLayoutUpdated;
            LayoutUpdated += OnAnchorCandidatesLayoutUpdated;
        }

        arrangedColumns = columns;
        arrangedRowPitch = rowPitch;
        return finalSize;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        scrollAnchorProvider = this.FindAncestorOfType<IScrollAnchorProvider>();
        InvalidateArrange();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        prepareBuffer = false;
        LayoutUpdated -= OnLayoutUpdated;
        LayoutUpdated -= OnAnchorCandidatesLayoutUpdated;

        foreach (var candidate in anchorCandidates)
        {
            scrollAnchorProvider?.UnregisterAnchorCandidate(candidate);
        }

        anchorCandidates.Clear();
        scrollAnchorProvider = null;
        viewport = UnknownViewport;
        arrangedColumns = 0;
        arrangedRowPitch = 0;
    }

    protected override void OnItemsChanged(IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs e)
    {
        // Appending past the realized window leaves every realized index valid, which is the shape
        // a library page load takes (clear, then add tile by tile). Anything else may shift indices
        // under the realized containers, so drop them and let the next measure rebuild the window.
        var appendedPastWindow = e.Action == NotifyCollectionChangedAction.Add &&
            e.NewStartingIndex >= 0 &&
            !HasRealizedFrom(e.NewStartingIndex);

        if (!appendedPastWindow)
        {
            prepareBuffer = false;
            UnrealizeAllExcept(-1, -1);
            FlushPendingRecycle();
            realizedFirstRow = -1;
            realizedLastRow = -1;
            realizedFirstIndex = -1;
            realizedLastIndex = -1;
        }

        InvalidateMeasure();
    }

    protected override void OnItemsControlChanged(ItemsControl? oldValue)
    {
        base.OnItemsControlChanged(oldValue);
        prepareBuffer = false;
        UnrealizeAllExcept(-1, -1);
        FlushPendingRecycle();
        recyclePool.Clear();
        realizedFirstRow = -1;
        realizedLastRow = -1;
        realizedFirstIndex = -1;
        realizedLastIndex = -1;
        RealizationWindowSize = 1;
    }

    protected override Control? ContainerFromIndex(int index) =>
        realized.TryGetValue(index, out var entry) ? entry.Control : null;

    protected override int IndexFromContainer(Control container) =>
        indexByContainer.TryGetValue(container, out var index) ? index : -1;

    protected override IEnumerable<Control>? GetRealizedContainers()
    {
        var containers = new List<Control>(realized.Count);

        foreach (var pair in realized)
        {
            containers.Add(pair.Value.Control);
        }

        return containers;
    }

    protected override Control? ScrollIntoView(int index)
    {
        if (index < 0 || index >= Items.Count)
        {
            return null;
        }

        if (ContainerFromIndex(index) is { } realizedContainer)
        {
            realizedContainer.BringIntoView();
            return realizedContainer;
        }

        var row = index / columns;
        var column = index - (row * columns);

        this.BringIntoView(new Rect(
            column * (itemWidth + MinColumnSpacing),
            row * (itemHeight + MinRowSpacing),
            itemWidth,
            itemHeight));

        UpdateLayout();

        return ContainerFromIndex(index);
    }

    protected override IInputElement? GetControl(NavigationDirection direction, IInputElement? from, bool wrap)
    {
        var count = Items.Count;

        if (count == 0)
        {
            return null;
        }

        var fromIndex = from is Control control ? IndexFromContainer(GetContainer(control)) : -1;

        var index = direction switch
        {
            NavigationDirection.First => 0,
            NavigationDirection.Last => count - 1,
            NavigationDirection.Next or NavigationDirection.Right => fromIndex + 1,
            NavigationDirection.Previous or NavigationDirection.Left => fromIndex - 1,
            NavigationDirection.Up => fromIndex - columns,
            NavigationDirection.Down => fromIndex + columns,
            _ => -1,
        };

        if (index < 0 || index >= count)
        {
            return null;
        }

        return ScrollIntoView(index);
    }

    /// <summary>
    /// Column count and cell size for a row of <paramref name="availableWidth"/>, following
    /// <c>UniformGridLayout</c> with <c>ItemsStretch="Uniform"</c>: fit as many minimum-width cells
    /// as the row holds, then hand each of them an integral share of what is left over and scale the
    /// height by the same ratio so the tile keeps its proportions.
    /// </summary>
    private void MeasureCells(double availableWidth, int count)
    {
        var minWidth = Math.Max(1d, MinItemWidth);
        var minHeight = Math.Max(1d, MinItemHeight);

        if (double.IsInfinity(availableWidth) || double.IsNaN(availableWidth))
        {
            // Unbounded width (a horizontal stack measuring its children) lays every item out in a
            // single row at its natural size, which is what the repeater did as well.
            columns = count;
            itemWidth = minWidth;
            itemHeight = minHeight;
            return;
        }

        var spacing = MinColumnSpacing;
        columns = Math.Max(1, (int)(availableWidth / (minWidth + spacing)));

        var used = (columns * (minWidth + spacing)) - spacing;
        var extra = Math.Max(0d, Math.Truncate((availableWidth - used) / columns));

        itemWidth = minWidth + extra;
        itemHeight = minHeight + (minHeight * (extra / minWidth));
    }

    private Rect GetLayoutViewport()
    {
        var rowPitch = itemHeight + MinRowSpacing;

        if ((columns != arrangedColumns || rowPitch != arrangedRowPitch) &&
            viewport != UnknownViewport &&
            scrollAnchorProvider?.CurrentAnchor is { } anchor &&
            indexByContainer.TryGetValue(anchor, out var anchorIndex))
        {
            var currentTop = (anchorIndex / columns) * rowPitch;
            return viewport.Translate(new Vector(0, currentTop - anchor.Bounds.Top));
        }

        return viewport;
    }

    private void GetVisibleRows(Rect viewport, int rows, out int firstRow, out int lastRow, int bufferRows = BufferRows)
    {
        if (viewport == UnknownViewport || viewport.Height <= 0 || double.IsInfinity(viewport.Height))
        {
            // Nothing to go on yet: realize the first row so the panel has something to show, and
            // rely on the viewport arriving at the end of this layout pass to widen the window.
            firstRow = 0;
            lastRow = 0;
            return;
        }

        var pitch = itemHeight + MinRowSpacing;

        firstRow = Math.Clamp(
            (int)Math.Floor(viewport.Top / pitch) - bufferRows,
            0,
            rows - 1);
        lastRow = Math.Clamp(
            (int)Math.Ceiling(viewport.Bottom / pitch) - 1 + bufferRows,
            firstRow,
            rows - 1);
    }

    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        if (!IsEffectivelyVisible || e.EffectiveViewport.Width <= 0 || e.EffectiveViewport.Height <= 0)
        {
            prepareBuffer = false;
            return;
        }

        var previous = viewport;
        viewport = e.EffectiveViewport;

        if (previous != UnknownViewport && previous.Top != viewport.Top)
        {
            scrollingForward = viewport.Top > previous.Top;
        }

        if (previous.Top == viewport.Top && previous.Bottom == viewport.Bottom)
        {
            return;
        }

        // A layout pass is only worth it when the scroll actually moved the realized window. Smooth
        // scrolling raises this event every frame, and re-measuring the panel each time invalidates
        // the arrange of every realized tile -- the cost of which grows with the column count, so
        // wide windows suffered the most.
        if (realizedFirstRow >= 0 && Items.Count > 0)
        {
            var rows = (Items.Count + columns - 1) / columns;
            var layoutViewport = GetLayoutViewport();
            GetVisibleRows(layoutViewport, rows, out var firstRow, out var lastRow, 0);

            if (firstRow >= realizedFirstRow && lastRow <= realizedLastRow)
            {
                var targetFirst = Math.Max(0, firstRow - BufferRows) * columns;
                var targetLast = Math.Min(Items.Count - 1, (Math.Min(rows - 1, lastRow + BufferRows) + 1) * columns - 1);

                if (previous.Top == viewport.Top ||
                    (targetFirst >= realizedFirstIndex && targetLast <= realizedLastIndex))
                {
                    return;
                }
            }

            if (layoutViewport != viewport)
            {
                LayoutUpdated -= OnLayoutUpdated;
                LayoutUpdated += OnLayoutUpdated;
                return;
            }
        }

        prepareBuffer = IsEffectivelyVisible && viewport.Width > 0 && viewport.Height > 0;
        InvalidateMeasure();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        LayoutUpdated -= OnLayoutUpdated;
        InvalidateMeasure();
    }

    private void OnAnchorCandidatesLayoutUpdated(object? sender, EventArgs e)
    {
        LayoutUpdated -= OnAnchorCandidatesLayoutUpdated;

        foreach (var pair in realized)
        {
            if (pair.Key % columns != 0 && anchorCandidates.Remove(pair.Value.Control))
            {
                scrollAnchorProvider?.UnregisterAnchorCandidate(pair.Value.Control);
            }
        }
    }

    private bool HasRealizedFrom(int index)
    {
        foreach (var pair in realized)
        {
            if (pair.Key >= index)
            {
                return true;
            }
        }

        return false;
    }

    private Control Realize(int index, IReadOnlyList<object?> items)
    {
        if (realized.TryGetValue(index, out var existing))
        {
            return existing.Control;
        }

        var generator = ItemContainerGenerator!;
        var item = items[index];

        if (!generator.NeedsContainer(item, index, out var recycleKey))
        {
            // The item is its own container: it can be prepared and added once, and from then on it
            // must stay realized.
            var self = (Control)item!;
            generator.PrepareItemContainer(self, item, index);
            AddInternalChild(self);
            generator.ItemContainerPrepared(self, item, index);
            realized[index] = new RealizedItem(self, null, true);
            indexByContainer[self] = index;
            return self;
        }

        Control container;

        if (recycleKey is not null && TakePendingRecycle(recycleKey) is { } reused)
        {
            // Still visible and still a child: the row that just left the window feeds the row that
            // is entering it, so nothing about the visual tree changes.
            container = reused;
            RecycledContainer.Prepare(generator, container, item, index);
            generator.ItemContainerPrepared(container, item, index);
        }
        else if (recycleKey is not null &&
            recyclePool.TryGetValue(recycleKey, out var pool) &&
            pool.Count > 0)
        {
            container = pool.Pop();
            container.IsVisible = true;
            RecycledContainer.Prepare(generator, container, item, index);
            generator.ItemContainerPrepared(container, item, index);
        }
        else
        {
            container = generator.CreateContainer(item, index, recycleKey);
            generator.PrepareItemContainer(container, item, index);
            AddInternalChild(container);
            generator.ItemContainerPrepared(container, item, index);
        }

        realized[index] = new RealizedItem(container, recycleKey, false);
        indexByContainer[container] = index;
        return container;
    }

    private void RealizeRange(int first, int last, IReadOnlyList<object?> items, Size cell)
    {
        for (var index = first; index <= last; ++index)
        {
            Realize(index, items).Measure(cell);
        }
    }

    /// <summary>Unrealizes every container outside <paramref name="first"/>..<paramref name="last"/>.</summary>
    private void UnrealizeAllExcept(int first, int last)
    {
        if (first >= 0 && realizedFirstIndex >= 0)
        {
            for (var index = realizedFirstIndex; index < first && index <= realizedLastIndex; ++index)
            {
                Unrealize(index);
            }

            for (var index = Math.Max(last + 1, realizedFirstIndex); index <= realizedLastIndex; ++index)
            {
                Unrealize(index);
            }

            return;
        }

        scratch.Clear();

        foreach (var pair in realized)
        {
            if (pair.Key < first || pair.Key > last)
            {
                scratch.Add(pair.Key);
            }
        }

        foreach (var index in scratch)
        {
            Unrealize(index);
        }

        scratch.Clear();
    }

    private void Unrealize(int index)
    {
        if (!realized.TryGetValue(index, out var entry) || entry.IsOwnContainer)
        {
            return;
        }

        if (anchorCandidates.Remove(entry.Control))
        {
            scrollAnchorProvider?.UnregisterAnchorCandidate(entry.Control);
        }

        realized.Remove(index);
        indexByContainer.Remove(entry.Control);

        if (entry.RecycleKey is null)
        {
            ItemContainerGenerator?.ClearItemContainer(entry.Control);
            RemoveInternalChild(entry.Control);
            return;
        }

        // Hiding and pooling is deferred: the containers this pass frees are usually the exact
        // ones the same pass needs again one row further down, and hiding one invalidates this
        // panel's layout again, which turned every row crossing into a double layout pass.
        //
        // A pooled container is deliberately left prepared. Clearing it nulls the ContentPresenter's
        // Content, which throws away the control the item template built, so every tile scrolled
        // back into view had to inflate a whole LibraryTile again -- around a millisecond each,
        // paid for a full row of tiles inside one synchronous measure. Keeping it prepared means
        // reuse only re-points the content, which the recycling template rebinds in place, and the
        // item this container last showed is released when the container is handed its next one.
        pendingRecycle.Add(entry);
    }

    private Control? TakePendingRecycle(object recycleKey)
    {
        for (var i = pendingRecycle.Count - 1; i >= 0; --i)
        {
            if (Equals(pendingRecycle[i].RecycleKey, recycleKey))
            {
                var control = pendingRecycle[i].Control;
                pendingRecycle.RemoveAt(i);
                return control;
            }
        }

        return null;
    }

    /// <summary>Hides and pools whatever <see cref="UnrealizeAllExcept"/> freed but nothing reused.</summary>
    private void FlushPendingRecycle()
    {
        foreach (var entry in pendingRecycle)
        {
            entry.Control.IsVisible = false;

            if (!recyclePool.TryGetValue(entry.RecycleKey!, out var pool))
            {
                pool = new Stack<Control>();
                recyclePool.Add(entry.RecycleKey!, pool);
            }

            pool.Push(entry.Control);
        }

        pendingRecycle.Clear();
    }

    /// <summary>Walks up from a focused descendant to the container this panel owns.</summary>
    private Control GetContainer(Control control)
    {
        var current = control;

        while (current is not null && !ReferenceEquals(current.GetVisualParent(), this))
        {
            current = current.GetVisualParent() as Control;
        }

        return current ?? control;
    }

    private readonly record struct RealizedItem(Control Control, object? RecycleKey, bool IsOwnContainer);
}
