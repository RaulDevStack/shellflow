using ShellFlow.Core.Layout;

namespace ShellFlow.Tests.Layout;

public sealed class PaneTreeTests
{
    private static PaneLeaf Leaf(string id) => new() { Id = id, ProfileId = "cmd" };

    [Fact]
    public void Split_ReplacesTargetWithSplitAndKeepsOrder()
    {
        PaneNode root = Leaf("a");
        root = PaneTree.Split(root, "a", PaneArrangement.SideBySide, Leaf("b"));
        root = PaneTree.Split(root, "b", PaneArrangement.Stacked, Leaf("c"));

        Assert.Equal(["a", "b", "c"], PaneTree.Leaves(root).Select(l => l.Id));
        var split = Assert.IsType<PaneSplit>(root);
        Assert.Equal(PaneArrangement.SideBySide, split.Arrangement);
        Assert.IsType<PaneSplit>(split.Second);
    }

    [Fact]
    public void Split_UnknownTarget_Throws()
    {
        Assert.Throws<ArgumentException>(() => PaneTree.Split(Leaf("a"), "zzz", PaneArrangement.Stacked, Leaf("b")));
    }

    [Fact]
    public void Remove_CollapsesParentIntoSibling()
    {
        PaneNode root = Leaf("a");
        root = PaneTree.Split(root, "a", PaneArrangement.SideBySide, Leaf("b"));
        root = PaneTree.Split(root, "b", PaneArrangement.Stacked, Leaf("c"));

        root = PaneTree.Remove(root, "b")!;

        Assert.Equal(["a", "c"], PaneTree.Leaves(root).Select(l => l.Id));
        var split = Assert.IsType<PaneSplit>(root);
        Assert.Equal("c", Assert.IsType<PaneLeaf>(split.Second).Id);
    }

    [Fact]
    public void Remove_LastLeaf_ReturnsNull()
    {
        Assert.Null(PaneTree.Remove(Leaf("a"), "a"));
    }

    [Fact]
    public void Remove_UnknownId_LeavesTreeUntouched()
    {
        var root = PaneTree.Split(Leaf("a"), "a", PaneArrangement.SideBySide, Leaf("b"));
        Assert.Same(root, PaneTree.Remove(root, "nope"));
        Assert.Equal(2, PaneTree.Count(root));
    }

    [Fact]
    public void FindNeighbor_FollowsGeometry()
    {
        // ┌───┬───┐
        // │ a │ b │
        // │   ├───┤
        // │   │ c │
        // └───┴───┘
        PaneNode root = Leaf("a");
        root = PaneTree.Split(root, "a", PaneArrangement.SideBySide, Leaf("b"));
        root = PaneTree.Split(root, "b", PaneArrangement.Stacked, Leaf("c"));

        Assert.Equal("b", PaneTree.FindNeighbor(root, "c", FocusDirection.Up));
        Assert.Equal("c", PaneTree.FindNeighbor(root, "b", FocusDirection.Down));
        Assert.Equal("a", PaneTree.FindNeighbor(root, "c", FocusDirection.Left));
        Assert.Null(PaneTree.FindNeighbor(root, "a", FocusDirection.Left));
        Assert.Null(PaneTree.FindNeighbor(root, "b", FocusDirection.Up));
        Assert.Contains(PaneTree.FindNeighbor(root, "a", FocusDirection.Right), new[] { "b", "c" });
    }

    [Fact]
    public void NextLeafId_Wraps()
    {
        var root = PaneTree.Split(PaneTree.Split(Leaf("a"), "a", PaneArrangement.SideBySide, Leaf("b")), "b", PaneArrangement.Stacked, Leaf("c"));
        Assert.Equal("a", PaneTree.NextLeafId(root, "c", 1));
        Assert.Equal("c", PaneTree.NextLeafId(root, "a", -1));
    }

    [Fact]
    public void Resize_MovesNearestMatchingDividerAndClamps()
    {
        var root = (PaneSplit)PaneTree.Split(Leaf("a"), "a", PaneArrangement.SideBySide, Leaf("b"));

        Assert.True(PaneTree.Resize(root, "a", FocusDirection.Right, 0.1));
        Assert.Equal(0.6, root.Ratio, 3);

        Assert.True(PaneTree.Resize(root, "b", FocusDirection.Left, 0.2));
        Assert.Equal(0.4, root.Ratio, 3);

        // No stacked divider exists for vertical resizing.
        Assert.False(PaneTree.Resize(root, "a", FocusDirection.Up, 0.1));

        for (var i = 0; i < 50; i++)
        {
            PaneTree.Resize(root, "a", FocusDirection.Right, 0.1);
        }

        Assert.Equal(PaneSplit.MaxRatio, root.Ratio, 3);
    }

    [Fact]
    public void Clone_IsDeep()
    {
        var root = PaneTree.Split(Leaf("a"), "a", PaneArrangement.SideBySide, Leaf("b"));
        var clone = (PaneSplit)PaneTree.Clone(root);
        ((PaneSplit)root).Ratio = 0.9;
        ((PaneLeaf)clone.First).ProfileId = "changed";

        Assert.Equal(0.5, clone.Ratio);
        Assert.Equal("cmd", PaneTree.FindLeaf(root, "a")!.ProfileId);
    }
}

public sealed class PaneLayoutEngineTests
{
    private static PaneLeaf Leaf(string id) => new() { Id = id, ProfileId = "cmd" };

    [Fact]
    public void SingleLeaf_FillsBounds()
    {
        var layout = PaneLayoutEngine.Compute(Leaf("a"), new LayoutRect(0, 0, 800, 600), gap: 6);
        Assert.Equal(new LayoutRect(0, 0, 800, 600), layout.Panes["a"]);
        Assert.Empty(layout.Splitters);
    }

    [Fact]
    public void SideBySide_SplitsWidthAroundGap()
    {
        var root = PaneTree.Split(Leaf("a"), "a", PaneArrangement.SideBySide, Leaf("b"));
        var layout = PaneLayoutEngine.Compute(root, new LayoutRect(0, 0, 806, 600), gap: 6);

        Assert.Equal(400, layout.Panes["a"].Width);
        Assert.Equal(406, layout.Panes["b"].X);
        Assert.Equal(400, layout.Panes["b"].Width);
        Assert.Equal(new LayoutRect(400, 0, 6, 600), Assert.Single(layout.Splitters).Bounds);
    }

    [Fact]
    public void MinimumSizes_AreRespectedWhenSpaceAllows()
    {
        var root = (PaneSplit)PaneTree.Split(Leaf("a"), "a", PaneArrangement.SideBySide, Leaf("b"));
        root.Ratio = 0.05;

        var layout = PaneLayoutEngine.Compute(root, new LayoutRect(0, 0, 1000, 600), gap: 0, minWidth: 160, minHeight: 90);

        Assert.Equal(160, layout.Panes["a"].Width);
        Assert.Equal(840, layout.Panes["b"].Width);
    }

    [Fact]
    public void NestedMinimums_AccountForWholeSubtree()
    {
        // b|c side by side inside the right half: the right half needs room for two minimum panes.
        PaneNode root = Leaf("a");
        root = PaneTree.Split(root, "a", PaneArrangement.SideBySide, Leaf("b"));
        root = PaneTree.Split(root, "b", PaneArrangement.SideBySide, Leaf("c"));
        ((PaneSplit)root).Ratio = 0.95;

        var layout = PaneLayoutEngine.Compute(root, new LayoutRect(0, 0, 1000, 600), gap: 0, minWidth: 160, minHeight: 90);

        Assert.True(layout.Panes["b"].Width >= 160);
        Assert.True(layout.Panes["c"].Width >= 160);
        Assert.Equal(1000, layout.Panes.Values.Sum(r => r.Width), 3);
    }

    [Fact]
    public void TinyBounds_DoNotProduceNegativeSizes()
    {
        var root = PaneTree.Split(Leaf("a"), "a", PaneArrangement.Stacked, Leaf("b"));
        var layout = PaneLayoutEngine.Compute(root, new LayoutRect(0, 0, 50, 20), gap: 6, minWidth: 160, minHeight: 90);
        Assert.All(layout.Panes.Values, r => Assert.True(r.Width >= 0 && r.Height >= 0));
    }

    [Fact]
    public void SplitterRatioFromPosition_ClampsToMinimums()
    {
        var root = PaneTree.Split(Leaf("a"), "a", PaneArrangement.SideBySide, Leaf("b"));
        var layout = PaneLayoutEngine.Compute(root, new LayoutRect(0, 0, 1006, 600), gap: 6, minWidth: 100);
        var splitter = Assert.Single(layout.Splitters);

        Assert.Equal(0.5, splitter.RatioFromPosition(503), 2);
        Assert.Equal(0.1, splitter.RatioFromPosition(0), 2);
        Assert.Equal(0.9, splitter.RatioFromPosition(5000), 2);
    }

    [Fact]
    public void InvalidRatio_FallsBackToEvenSplit()
    {
        var root = (PaneSplit)PaneTree.Split(Leaf("a"), "a", PaneArrangement.Stacked, Leaf("b"));
        root.Ratio = double.NaN;
        var layout = PaneLayoutEngine.Compute(root, new LayoutRect(0, 0, 100, 200));
        Assert.Equal(100, layout.Panes["a"].Height);
    }
}
