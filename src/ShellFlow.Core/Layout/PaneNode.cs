using System.Text.Json.Serialization;

namespace ShellFlow.Core.Layout;

/// <summary>How the two children of a split are arranged.</summary>
public enum PaneArrangement
{
    /// <summary>Children sit next to each other, separated by a vertical divider ("split right").</summary>
    SideBySide,

    /// <summary>Children are stacked, separated by a horizontal divider ("split down").</summary>
    Stacked,
}

public enum FocusDirection
{
    Left,
    Right,
    Up,
    Down,
}

/// <summary>A node in a tab's binary split tree.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(PaneLeaf), "pane")]
[JsonDerivedType(typeof(PaneSplit), "split")]
public abstract class PaneNode
{
}

/// <summary>A single terminal pane.</summary>
public sealed class PaneLeaf : PaneNode
{
    public string Id { get; set; } = NewId();

    public string ProfileId { get; set; } = string.Empty;

    /// <summary>Directory the pane's shell starts in. Null uses the profile or global default.</summary>
    public string? StartingDirectory { get; set; }

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];
}

public sealed class PaneSplit : PaneNode
{
    public const double MinRatio = 0.05;
    public const double MaxRatio = 0.95;

    public PaneArrangement Arrangement { get; set; }

    /// <summary>Fraction of the available space given to <see cref="First"/>.</summary>
    public double Ratio { get; set; } = 0.5;

    public required PaneNode First { get; set; }

    public required PaneNode Second { get; set; }
}
