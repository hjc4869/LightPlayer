using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace LightStudio.LightPlayer.Controls;

/// <summary>
/// Lays out a setting's header and description beside its action. A row that declares
/// <see cref="ActionWidth"/> gives the action that width, so the actions of a group line up;
/// other actions keep their own size. In the narrow layout (<see cref="IsNarrowLayoutProperty"/>)
/// the action moves below the header and description instead: at the full row width for a row
/// with a declared width, when the header would not fit on one line beside it or the description
/// would take two more lines there than across the row, and at its own size for other actions,
/// once they would take more than half of the row. The rows with a declared width inside one
/// group (<see cref="IsRowGroupProperty"/>) move their actions below together. Expects three
/// children: the header, the description, and the action. Carries the <c>stacked</c> class while
/// the action sits below.
/// </summary>
public sealed class SettingRowPanel : Panel
{
    public static readonly StyledProperty<double> ActionWidthProperty =
        AvaloniaProperty.Register<SettingRowPanel, double>(nameof(ActionWidth), double.NaN);

    public static readonly StyledProperty<double> ActionWidthScaleProperty =
        AvaloniaProperty.Register<SettingRowPanel, double>(nameof(ActionWidthScale), 1d);

    public static readonly StyledProperty<double> ActionSpacingProperty =
        AvaloniaProperty.Register<SettingRowPanel, double>(nameof(ActionSpacing), 16d);

    /// <summary>
    /// Set on the action of a row that declares <see cref="ActionWidth"/>: what the row does when
    /// the action's content needs another width.
    /// </summary>
    public static readonly AttachedProperty<SettingActionSizing> ActionSizingProperty =
        AvaloniaProperty.RegisterAttached<SettingRowPanel, Control, SettingActionSizing>("ActionSizing");

    /// <summary>
    /// Set on a host of setting rows while it shows its narrow layout; the rows inside inherit it
    /// and move their actions below their headers where those would not fit side by side.
    /// </summary>
    public static readonly AttachedProperty<bool> IsNarrowLayoutProperty =
        AvaloniaProperty.RegisterAttached<SettingRowPanel, Control, bool>("IsNarrowLayout", inherits: true);

    /// <summary>
    /// Set on the container of a block of setting rows: once one row with a declared action width
    /// inside has to move its action below, all of them do.
    /// </summary>
    public static readonly AttachedProperty<bool> IsRowGroupProperty =
        AvaloniaProperty.RegisterAttached<SettingRowPanel, Control, bool>("IsRowGroup");

    /// <summary>
    /// False on an action that moves below its header without taking the other actions of its
    /// group along, as a slider does. It still follows them.
    /// </summary>
    public static readonly AttachedProperty<bool> StacksGroupProperty =
        AvaloniaProperty.RegisterAttached<SettingRowPanel, Control, bool>("StacksGroup", defaultValue: true);

    private static readonly AttachedProperty<RowGroup?> RowGroupProperty =
        AvaloniaProperty.RegisterAttached<SettingRowPanel, Control, RowGroup?>("RowGroup");

    private const double StackedSpacing = 8;
    private const double LabelSpacing = 2;
    private const double MinimumLabelShare = 0.5;

    // In header line heights: one more description line beside the action is fine, two are not.
    private const double SqueezeTolerance = 1.5;

    private bool isStacked;
    private bool fillsRow;
    private bool needsStack;
    private double inlineActionWidth;
    private RowGroup? group;

    static SettingRowPanel()
    {
        AffectsMeasure<SettingRowPanel>(ActionWidthProperty, ActionWidthScaleProperty, ActionSpacingProperty,
            IsNarrowLayoutProperty);
    }

    /// <summary>Width of the action beside the header, or NaN for an action that keeps its own size.</summary>
    public double ActionWidth
    {
        get => GetValue(ActionWidthProperty);
        set => SetValue(ActionWidthProperty, value);
    }

    /// <summary>Scales <see cref="ActionWidth"/> for styles with larger control text.</summary>
    public double ActionWidthScale
    {
        get => GetValue(ActionWidthScaleProperty);
        set => SetValue(ActionWidthScaleProperty, value);
    }

    /// <summary>Gap between the header and the action beside it.</summary>
    public double ActionSpacing
    {
        get => GetValue(ActionSpacingProperty);
        set => SetValue(ActionSpacingProperty, value);
    }

    public static SettingActionSizing GetActionSizing(Control control) => control.GetValue(ActionSizingProperty);

    public static void SetActionSizing(Control control, SettingActionSizing value) =>
        control.SetValue(ActionSizingProperty, value);

    public static bool GetIsNarrowLayout(Control control) => control.GetValue(IsNarrowLayoutProperty);

    public static void SetIsNarrowLayout(Control control, bool value) => control.SetValue(IsNarrowLayoutProperty, value);

    public static bool GetStacksGroup(Control control) => control.GetValue(StacksGroupProperty);

    public static void SetStacksGroup(Control control, bool value) => control.SetValue(StacksGroupProperty, value);

    public static bool GetIsRowGroup(Control control) => control.GetValue(IsRowGroupProperty);

    public static void SetIsRowGroup(Control control, bool value) => control.SetValue(IsRowGroupProperty, value);

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        JoinGroup(false, false, false);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count != 3)
        {
            return base.MeasureOverride(availableSize);
        }

        Control header = Children[0], action = Children[2];
        Control actionRoot = action is ContentControl { Content: Control content } ? content : action;
        double width = availableSize.Width;
        double declared = ActionWidth * ActionWidthScale;
        bool adapts = GetIsNarrowLayout(this) && double.IsFinite(width);
        bool stacked = false;
        double labelHeight;

        fillsRow = !double.IsNaN(declared) && double.IsFinite(width);
        if (fillsRow)
        {
            var sizing = GetActionSizing(actionRoot);
            if (sizing == SettingActionSizing.Hug || (sizing == SettingActionSizing.Fit && adapts))
            {
                // A spare DIP keeps pixel rounding inside the action from trimming its text.
                action.Measure(Size.Infinity);
                double natural = Math.Ceiling(action.DesiredSize.Width) + 1;
                if (sizing == SettingActionSizing.Hug)
                {
                    declared = natural;
                }
                else
                {
                    stacked = natural > declared;
                }
            }

            inlineActionWidth = Math.Min(declared, width);

            // A hidden action keeps no slot.
            action.Measure(new Size(inlineActionWidth, availableSize.Height));
            fillsRow = action.DesiredSize.Height > 0;
        }

        if (fillsRow)
        {
            double labelWidth = Math.Max(0, width - ReservedWidth(inlineActionWidth));
            if (adapts && !stacked)
            {
                header.Measure(Size.Infinity);
                double headerLine = header.DesiredSize.Height;
                stacked = header.DesiredSize.Width > labelWidth
                    || MeasureLabel(labelWidth) - MeasureLabel(width) > headerLine * SqueezeTolerance;
            }

            stacked = JoinGroup(adapts, stacked, GetStacksGroup(actionRoot));
            labelHeight = MeasureLabel(stacked ? width : labelWidth);
            if (stacked)
            {
                action.Measure(new Size(width, availableSize.Height));
            }
        }
        else
        {
            JoinGroup(false, false, false);
            action.Measure(availableSize);
            inlineActionWidth = action.DesiredSize.Width;
            double labelWidth = Math.Max(0, width - ReservedWidth(inlineActionWidth));
            stacked = adapts && labelWidth < width * MinimumLabelShare;
            labelHeight = MeasureLabel(stacked ? width : labelWidth);
        }

        if (isStacked != stacked)
        {
            isStacked = stacked;
            Classes.Set("stacked", stacked);
        }

        double labelWidthUsed = Math.Max(header.DesiredSize.Width, Children[1].DesiredSize.Width);
        return stacked
            ? new Size(Math.Max(labelWidthUsed, action.DesiredSize.Width),
                labelHeight + StackedSpacing + action.DesiredSize.Height)
            : new Size(labelWidthUsed + ReservedWidth(inlineActionWidth),
                Math.Max(labelHeight, action.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count != 3)
        {
            return base.ArrangeOverride(finalSize);
        }

        Control header = Children[0], description = Children[1], action = Children[2];
        double labelHeight = LabelHeight(header, description);
        double actionHeight = action.DesiredSize.Height;

        if (isStacked)
        {
            double top = Math.Max(0, (finalSize.Height - labelHeight - StackedSpacing - actionHeight) / 2);
            ArrangeLabel(header, description, finalSize.Width, top);
            double actionWidth = fillsRow ? finalSize.Width : Math.Min(action.DesiredSize.Width, finalSize.Width);
            action.Arrange(new Rect(0, top + labelHeight + StackedSpacing, actionWidth, actionHeight));
        }
        else
        {
            double actionWidth = Math.Min(inlineActionWidth, finalSize.Width);
            ArrangeLabel(header, description, Math.Max(0, finalSize.Width - ReservedWidth(actionWidth)),
                Math.Max(0, (finalSize.Height - labelHeight) / 2));
            action.Arrange(new Rect(finalSize.Width - actionWidth, Math.Max(0, (finalSize.Height - actionHeight) / 2),
                actionWidth, actionHeight));
        }

        return finalSize;
    }

    private double ReservedWidth(double actionWidth) => actionWidth > 0 ? actionWidth + ActionSpacing : 0;

    // Records whether this row makes its group stack and returns whether it stacks, on its own account or its group's.
    private bool JoinGroup(bool adapts, bool needs, bool leads)
    {
        RowGroup? found = adapts ? RowGroup.Find(this) : null;
        if (group != found)
        {
            group?.Remove(this);
            group = found;
            group?.Add(this);
        }

        needsStack = needs && leads;
        group?.Refresh(this);
        return needs || group is { IsStacked: true };
    }

    private double MeasureLabel(double width)
    {
        Control header = Children[0], description = Children[1];
        var constraint = new Size(width, double.PositiveInfinity);
        header.Measure(constraint);
        description.Measure(constraint);
        return LabelHeight(header, description);
    }

    private static double LabelHeight(Control header, Control description)
    {
        double headerHeight = header.DesiredSize.Height, descriptionHeight = description.DesiredSize.Height;
        return headerHeight + descriptionHeight + (headerHeight > 0 && descriptionHeight > 0 ? LabelSpacing : 0);
    }

    private static void ArrangeLabel(Control header, Control description, double width, double top)
    {
        double headerHeight = header.DesiredSize.Height;
        header.Arrange(new Rect(0, top, width, headerHeight));
        description.Arrange(new Rect(0, top + headerHeight + (headerHeight > 0 ? LabelSpacing : 0), width,
            description.DesiredSize.Height));
    }

    /// <summary>The rows of one group that declare an action width, and whether any of them has to stack.</summary>
    private sealed class RowGroup
    {
        private readonly List<SettingRowPanel> rows = [];

        private RowGroup(Control host)
        {
            // A row that gets hidden is not measured again; its group's host changes size instead.
            host.PropertyChanged += (_, e) =>
            {
                if (e.Property == BoundsProperty)
                {
                    Refresh(null);
                }
            };
        }

        public bool IsStacked { get; private set; }

        public static RowGroup? Find(SettingRowPanel row)
        {
            for (Visual? visual = row.GetVisualParent(); visual is not null; visual = visual.GetVisualParent())
            {
                if (visual is Control host && GetIsRowGroup(host))
                {
                    var group = host.GetValue(RowGroupProperty);
                    if (group is null)
                    {
                        group = new RowGroup(host);
                        host.SetValue(RowGroupProperty, group);
                    }

                    return group;
                }
            }

            return null;
        }

        public void Add(SettingRowPanel row) => rows.Add(row);

        public void Remove(SettingRowPanel row)
        {
            rows.Remove(row);
            Refresh(null);
        }

        public void Refresh(SettingRowPanel? measuring)
        {
            bool stacked = false;
            foreach (var row in rows)
            {
                if (row.needsStack && row.IsEffectivelyVisible)
                {
                    stacked = true;
                    break;
                }
            }

            if (IsStacked == stacked)
            {
                return;
            }

            IsStacked = stacked;
            foreach (var row in rows)
            {
                if (row != measuring)
                {
                    row.InvalidateMeasure();
                }
            }
        }
    }
}

/// <summary>What a <see cref="SettingRowPanel"/> does when an action's content needs another width than the declared one.</summary>
public enum SettingActionSizing
{
    /// <summary>The declared width, whatever the content.</summary>
    Fixed,

    /// <summary>The declared width; in the narrow layout the action moves below the header when the content needs more.</summary>
    Fit,

    /// <summary>The width of the content.</summary>
    Hug,
}
