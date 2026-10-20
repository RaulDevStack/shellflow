namespace ShellFlow.Core.Layout;

/// <summary>Operations on a tab's split tree. Methods that can replace the root return the new root.</summary>
public static class PaneTree
{
    public static IEnumerable<PaneLeaf> Leaves(PaneNode? node)
    {
        switch (node)
        {
            case PaneLeaf leaf:
                yield return leaf;
                break;
            case PaneSplit split:
                foreach (var l in Leaves(split.First))
                {
                    yield return l;
                }

                foreach (var l in Leaves(split.Second))
                {
                    yield return l;
                }

                break;
        }
    }

    public static PaneLeaf? FindLeaf(PaneNode? root, string id) =>
        Leaves(root).FirstOrDefault(l => string.Equals(l.Id, id, StringComparison.Ordinal));

    public static int Count(PaneNode? root) => Leaves(root).Count();

    /// <summary>Replaces the target leaf with a split containing it and <paramref name="newLeaf"/> (placed second).</summary>
    public static PaneNode Split(PaneNode root, string targetId, PaneArrangement arrangement, PaneLeaf newLeaf)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(newLeaf);
        var target = FindLeaf(root, targetId) ?? throw new ArgumentException($"Pane '{targetId}' is not in this tree.", nameof(targetId));
        var split = new PaneSplit { Arrangement = arrangement, Ratio = 0.5, First = target, Second = newLeaf };
        return Replace(root, target, split);
    }

    /// <summary>Removes a leaf, collapsing its parent split into the sibling. Returns null when the tree becomes empty.</summary>
    public static PaneNode? Remove(PaneNode root, string id)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root is PaneLeaf rootLeaf)
        {
            return rootLeaf.Id == id ? null : root;
        }

        var target = FindLeaf(root, id);
        if (target is null)
        {
            return root;
        }

        var parent = FindParent(root, target)!;
        var sibling = ReferenceEquals(parent.First, target) ? parent.Second : parent.First;
        return Replace(root, parent, sibling);
    }

    public static PaneSplit? FindParent(PaneNode root, PaneNode child)
    {
        if (root is not PaneSplit split)
        {
            return null;
        }

        if (ReferenceEquals(split.First, child) || ReferenceEquals(split.Second, child))
        {
            return split;
        }

        return FindParent(split.First, child) ?? FindParent(split.Second, child);
    }

    /// <summary>Finds the pane geometrically adjacent in a direction, preferring the one best aligned with the current pane.</summary>
    public static string? FindNeighbor(PaneNode root, string id, FocusDirection direction)
    {
        var layout = PaneLayoutEngine.Compute(root, new LayoutRect(0, 0, 10000, 10000));
        if (!layout.Panes.TryGetValue(id, out var current))
        {
            return null;
        }

        const double epsilon = 0.5;
        string? best = null;
        var bestDistance = double.MaxValue;
        var bestAlignment = double.MaxValue;

        foreach (var (otherId, other) in layout.Panes)
        {
            if (otherId == id)
            {
                continue;
            }

            double distance;
            double overlap;
            double alignment;
            switch (direction)
            {
                case FocusDirection.Left when other.Right <= current.X + epsilon:
                    distance = current.X - other.Right;
                    overlap = Math.Min(current.Bottom, other.Bottom) - Math.Max(current.Y, other.Y);
                    alignment = Math.Abs(current.CenterY - other.CenterY);
                    break;
                case FocusDirection.Right when other.X >= current.Right - epsilon:
                    distance = other.X - current.Right;
                    overlap = Math.Min(current.Bottom, other.Bottom) - Math.Max(current.Y, other.Y);
                    alignment = Math.Abs(current.CenterY - other.CenterY);
                    break;
                case FocusDirection.Up when other.Bottom <= current.Y + epsilon:
                    distance = current.Y - other.Bottom;
                    overlap = Math.Min(current.Right, other.Right) - Math.Max(current.X, other.X);
                    alignment = Math.Abs(current.CenterX - other.CenterX);
                    break;
                case FocusDirection.Down when other.Y >= current.Bottom - epsilon:
                    distance = other.Y - current.Bottom;
                    overlap = Math.Min(current.Right, other.Right) - Math.Max(current.X, other.X);
                    alignment = Math.Abs(current.CenterX - other.CenterX);
                    break;
                default:
                    continue;
            }

            if (overlap <= epsilon)
            {
                continue;
            }

            if (distance < bestDistance - epsilon || (Math.Abs(distance - bestDistance) <= epsilon && alignment < bestAlignment))
            {
                best = otherId;
                bestDistance = distance;
                bestAlignment = alignment;
            }
        }

        return best;
    }

    /// <summary>Cyclic pane order (reading order of the tree).</summary>
    public static string? NextLeafId(PaneNode root, string id, int offset)
    {
        var ids = Leaves(root).Select(l => l.Id).ToList();
        if (ids.Count == 0)
        {
            return null;
        }

        var index = ids.IndexOf(id);
        if (index < 0)
        {
            return ids[0];
        }

        return ids[(((index + offset) % ids.Count) + ids.Count) % ids.Count];
    }

    /// <summary>
    /// Moves the divider nearest to a pane in the given direction by <paramref name="step"/> (a ratio delta).
    /// Prefers the divider on that side of the pane; falls back to the opposite one. Returns false if no divider moves.
    /// </summary>
    public static bool Resize(PaneNode root, string id, FocusDirection direction, double step)
    {
        var leaf = FindLeaf(root, id);
        if (leaf is null)
        {
            return false;
        }

        var axis = direction is FocusDirection.Left or FocusDirection.Right ? PaneArrangement.SideBySide : PaneArrangement.Stacked;
        var towardsEnd = direction is FocusDirection.Right or FocusDirection.Down;

        PaneSplit? preferred = null;
        PaneSplit? fallback = null;
        PaneNode child = leaf;
        for (var parent = FindParent(root, child); parent is not null; child = parent, parent = FindParent(root, parent))
        {
            if (parent.Arrangement != axis)
            {
                continue;
            }

            // The divider lies after the child when the child is First.
            var dividerInDirection = ReferenceEquals(parent.First, child) == towardsEnd;
            if (dividerInDirection)
            {
                preferred ??= parent;
            }
            else
            {
                fallback ??= parent;
            }
        }

        var split = preferred ?? fallback;
        if (split is null)
        {
            return false;
        }

        var ratio = Math.Clamp(split.Ratio + (towardsEnd ? step : -step), PaneSplit.MinRatio, PaneSplit.MaxRatio);
        if (Math.Abs(ratio - split.Ratio) < 1e-9)
        {
            return false;
        }

        split.Ratio = ratio;
        return true;
    }

    /// <summary>Resets every split in the tree to an even ratio.</summary>
    public static void Equalize(PaneNode? node)
    {
        if (node is PaneSplit split)
        {
            split.Ratio = 0.5;
            Equalize(split.First);
            Equalize(split.Second);
        }
    }

    public static PaneNode Clone(PaneNode node) => node switch
    {
        PaneLeaf leaf => new PaneLeaf { Id = leaf.Id, ProfileId = leaf.ProfileId, StartingDirectory = leaf.StartingDirectory },
        PaneSplit split => new PaneSplit { Arrangement = split.Arrangement, Ratio = split.Ratio, First = Clone(split.First), Second = Clone(split.Second) },
        _ => throw new NotSupportedException(node.GetType().Name),
    };

    private static PaneNode Replace(PaneNode root, PaneNode target, PaneNode replacement)
    {
        if (ReferenceEquals(root, target))
        {
            return replacement;
        }

        var parent = FindParent(root, target) ?? throw new InvalidOperationException("Node is not part of the tree.");
        if (ReferenceEquals(parent.First, target))
        {
            parent.First = replacement;
        }
        else
        {
            parent.Second = replacement;
        }

        return root;
    }
}
