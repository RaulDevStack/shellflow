namespace ShellFlow.Core.Layout;

public readonly record struct LayoutRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double CenterX => X + (Width / 2);

    public double CenterY => Y + (Height / 2);
}

/// <summary>The divider between the two children of a split, in layout coordinates.</summary>
public sealed record SplitterLayout(PaneSplit Split, LayoutRect Bounds, LayoutRect Container, double Gap, double MinFirst, double MinSecond)
{
    public PaneArrangement Arrangement => Split.Arrangement;

    /// <summary>Converts a pointer position along the split axis into a ratio, respecting minimum pane sizes.</summary>
    public double RatioFromPosition(double position)
    {
        var start = Arrangement == PaneArrangement.SideBySide ? Container.X : Container.Y;
        var length = (Arrangement == PaneArrangement.SideBySide ? Container.Width : Container.Height) - Gap;
        if (length <= 0)
        {
            return Split.Ratio;
        }

        var first = position - start - (Gap / 2);
        first = ClampFirst(first, length, MinFirst, MinSecond);
        return Math.Clamp(first / length, PaneSplit.MinRatio, PaneSplit.MaxRatio);
    }

    internal static double ClampFirst(double first, double length, double minFirst, double minSecond)
    {
        if (length < minFirst + minSecond)
        {
            // Not enough room for both minimums: share proportionally instead of overflowing.
            return Math.Clamp(first, 0, length);
        }

        return Math.Clamp(first, minFirst, length - minSecond);
    }
}

public sealed class PaneLayout
{
    public Dictionary<string, LayoutRect> Panes { get; } = new(StringComparer.Ordinal);

    public List<SplitterLayout> Splitters { get; } = [];
}

public static class PaneLayoutEngine
{
    /// <summary>
    /// Computes pane rectangles for a split tree. <paramref name="gap"/> is the space reserved for each divider.
    /// Minimum sizes are honoured whenever the available space allows it.
    /// </summary>
    public static PaneLayout Compute(PaneNode root, LayoutRect bounds, double gap = 0, double minWidth = 0, double minHeight = 0)
    {
        ArgumentNullException.ThrowIfNull(root);
        var layout = new PaneLayout();
        Place(root, bounds, gap, minWidth, minHeight, layout);
        return layout;
    }

    /// <summary>Smallest size a subtree can take along an axis without violating the per-pane minimum.</summary>
    public static double MinimumExtent(PaneNode node, PaneArrangement axis, double gap, double minPane) => node switch
    {
        PaneLeaf => minPane,
        PaneSplit split when split.Arrangement == axis =>
            MinimumExtent(split.First, axis, gap, minPane) + gap + MinimumExtent(split.Second, axis, gap, minPane),
        PaneSplit split => Math.Max(MinimumExtent(split.First, axis, gap, minPane), MinimumExtent(split.Second, axis, gap, minPane)),
        _ => minPane,
    };

    private static void Place(PaneNode node, LayoutRect rect, double gap, double minWidth, double minHeight, PaneLayout layout)
    {
        switch (node)
        {
            case PaneLeaf leaf:
                layout.Panes[leaf.Id] = rect;
                break;

            case PaneSplit split when split.Arrangement == PaneArrangement.SideBySide:
            {
                var length = Math.Max(0, rect.Width - gap);
                var minFirst = MinimumExtent(split.First, PaneArrangement.SideBySide, gap, minWidth);
                var minSecond = MinimumExtent(split.Second, PaneArrangement.SideBySide, gap, minWidth);
                var first = SplitterLayout.ClampFirst(Math.Round(length * Ratio(split)), length, minFirst, minSecond);
                var firstRect = rect with { Width = first };
                var dividerRect = rect with { X = rect.X + first, Width = gap };
                var secondRect = rect with { X = rect.X + first + gap, Width = length - first };
                layout.Splitters.Add(new SplitterLayout(split, dividerRect, rect, gap, minFirst, minSecond));
                Place(split.First, firstRect, gap, minWidth, minHeight, layout);
                Place(split.Second, secondRect, gap, minWidth, minHeight, layout);
                break;
            }

            case PaneSplit split:
            {
                var length = Math.Max(0, rect.Height - gap);
                var minFirst = MinimumExtent(split.First, PaneArrangement.Stacked, gap, minHeight);
                var minSecond = MinimumExtent(split.Second, PaneArrangement.Stacked, gap, minHeight);
                var first = SplitterLayout.ClampFirst(Math.Round(length * Ratio(split)), length, minFirst, minSecond);
                var firstRect = rect with { Height = first };
                var dividerRect = rect with { Y = rect.Y + first, Height = gap };
                var secondRect = rect with { Y = rect.Y + first + gap, Height = length - first };
                layout.Splitters.Add(new SplitterLayout(split, dividerRect, rect, gap, minFirst, minSecond));
                Place(split.First, firstRect, gap, minWidth, minHeight, layout);
                Place(split.Second, secondRect, gap, minWidth, minHeight, layout);
                break;
            }
        }
    }

    private static double Ratio(PaneSplit split) =>
        double.IsFinite(split.Ratio) ? Math.Clamp(split.Ratio, PaneSplit.MinRatio, PaneSplit.MaxRatio) : 0.5;
}
