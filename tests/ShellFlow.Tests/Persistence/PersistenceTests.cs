using System.Text.Json;
using ShellFlow.Core.Layout;
using ShellFlow.Core.Persistence;
using ShellFlow.Core.Settings;
using ShellFlow.Core.Workspaces;

namespace ShellFlow.Tests.Persistence;

public sealed class PersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "shellflow-tests", Guid.NewGuid().ToString("N"));

    public PersistenceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private AppPaths Paths => new(_root);

    private static WorkspaceDocument SampleWorkspace()
    {
        PaneNode root = new PaneLeaf { Id = "a", ProfileId = "cmd", StartingDirectory = @"C:\src" };
        root = PaneTree.Split(root, "a", PaneArrangement.SideBySide, new PaneLeaf { Id = "b", ProfileId = "windows-powershell" });
        root = PaneTree.Split(root, "b", PaneArrangement.Stacked, new PaneLeaf { Id = "c", ProfileId = "git-bash" });
        ((PaneSplit)root).Ratio = 0.3;

        return new WorkspaceDocument
        {
            Id = "abc123",
            Name = "Backend",
            SelectedTabIndex = 1,
            Tabs =
            [
                new TabDocument { Id = "t1", Root = new PaneLeaf { Id = "x", ProfileId = "cmd" }, FocusedPaneId = "x" },
                new TabDocument { Id = "t2", Title = "Services", Root = root, FocusedPaneId = "c" },
            ],
        };
    }

    [Fact]
    public void Workspace_RoundTripsThroughStore()
    {
        var store = new WorkspaceStore(Paths);
        store.Save(SampleWorkspace());

        var loaded = store.Load("abc123");

        Assert.NotNull(loaded);
        Assert.Equal(LoadStatus.Loaded, loaded.Status);
        var ws = loaded.Value;
        Assert.Equal("Backend", ws.Name);
        Assert.Equal(1, ws.SelectedTabIndex);
        Assert.Equal("Services", ws.Tabs[1].Title);
        var split = Assert.IsType<PaneSplit>(ws.Tabs[1].Root);
        Assert.Equal(0.3, split.Ratio);
        Assert.Equal(PaneArrangement.SideBySide, split.Arrangement);
        Assert.Equal(["a", "b", "c"], PaneTree.Leaves(split).Select(l => l.Id));
        Assert.Equal(@"C:\src", PaneTree.FindLeaf(split, "a")!.StartingDirectory);
        Assert.NotNull(ws.LastSaved);
    }

    [Fact]
    public void Workspace_JsonUsesReadableDiscriminators()
    {
        var json = JsonSerializer.Serialize(SampleWorkspace(), ShellFlowJsonContext.Default.WorkspaceDocument);
        Assert.Contains("\"kind\": \"split\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"pane\"", json, StringComparison.Ordinal);
        Assert.Contains("\"arrangement\": \"SideBySide\"", json, StringComparison.Ordinal);
        Assert.Contains("\"schemaVersion\": 1", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Workspace_ListSkipsCorruptFilesAndOrdersByLastSaved()
    {
        var store = new WorkspaceStore(Paths);
        var older = SampleWorkspace();
        older.Id = "older1";
        older.Name = "Older";
        store.Save(older);
        Thread.Sleep(20);
        store.Save(SampleWorkspace());
        File.WriteAllText(Path.Combine(Paths.WorkspacesDirectory, "broken9.json"), "{ not json");

        var list = store.List();

        Assert.Equal(["Backend", "Older"], list.Select(s => s.Name));
        Assert.Equal(2, list[0].TabCount);
    }

    [Fact]
    public void Restore_ReplacesMissingProfilesAndRepairsStructure()
    {
        var ws = SampleWorkspace();
        ws.SelectedTabIndex = 99;
        ws.Tabs.Add(new TabDocument { Root = null, FocusedPaneId = "ghost" });
        ws.Tabs[1].FocusedPaneId = "missing";
        ((PaneSplit)ws.Tabs[1].Root!).Ratio = 7;
        PaneTree.FindLeaf(ws.Tabs[1].Root, "c")!.Id = "a"; // Duplicate id.

        var available = new HashSet<string> { "cmd", "windows-powershell" };
        var notes = WorkspaceValidator.Normalize(ws, available.Contains, "windows-powershell");

        Assert.Equal(2, ws.SelectedTabIndex);
        Assert.Contains(notes, n => n.Contains("git-bash", StringComparison.Ordinal));
        Assert.All(ws.Tabs, t => Assert.All(PaneTree.Leaves(t.Root), l => Assert.Contains(l.ProfileId, available)));
        var ids = ws.Tabs.SelectMany(t => PaneTree.Leaves(t.Root)).Select(l => l.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(PaneSplit.MaxRatio, ((PaneSplit)ws.Tabs[1].Root!).Ratio);
        Assert.NotNull(PaneTree.FindLeaf(ws.Tabs[1].Root, ws.Tabs[1].FocusedPaneId!));
        Assert.IsType<PaneLeaf>(ws.Tabs[2].Root);
    }

    [Fact]
    public void Restore_EmptyWorkspaceGetsOneTab()
    {
        var ws = new WorkspaceDocument { Name = "  ", Tabs = [] };
        WorkspaceValidator.Normalize(ws, _ => true, "cmd");
        Assert.Single(ws.Tabs);
        Assert.Equal("Workspace", ws.Name);
    }

    [Fact]
    public void Settings_MissingFileUsesDefaults()
    {
        var store = new SettingsStore(Paths);
        var settings = store.Load();
        Assert.Equal(13, settings.Terminal.FontSize);
        Assert.Empty(store.LoadNotes);
    }

    [Fact]
    public void Settings_CorruptFileIsBackedUpAndDefaultsUsed()
    {
        File.WriteAllText(Paths.SettingsFile, "{ \"terminal\": { \"fontSize\": ");
        var store = new SettingsStore(Paths);

        var settings = store.Load();

        Assert.Equal(13, settings.Terminal.FontSize);
        Assert.Single(store.LoadNotes);
        Assert.False(File.Exists(Paths.SettingsFile));
        Assert.Single(Directory.GetFiles(_root, "settings.corrupt-*.json"));
    }

    [Fact]
    public void Settings_WrongTypesAreRecovered()
    {
        File.WriteAllText(Paths.SettingsFile, "{ \"terminal\": { \"fontSize\": \"huge\" } }");
        var settings = new SettingsStore(Paths).Load();
        Assert.Equal(13, settings.Terminal.FontSize);
    }

    [Fact]
    public void Settings_OutOfRangeValuesAreClampedWithNotes()
    {
        File.WriteAllText(Paths.SettingsFile, """
            {
              // comments and trailing commas are tolerated
              "schemaVersion": 1,
              "terminal": { "fontSize": 400, "scrollback": -5, "fontFamily": "x; } body { color: red" },
              "keyBindings": { "pane.splitRight": "Ctrl+Alt+D", "tab.new": "NotAKey+++" },
            }
            """);
        var store = new SettingsStore(Paths);

        var settings = store.Load();

        Assert.Equal(TerminalSettings.MaxFontSize, settings.Terminal.FontSize);
        Assert.Equal(0, settings.Terminal.Scrollback);
        Assert.DoesNotContain(";", settings.Terminal.FontFamily, StringComparison.Ordinal);
        Assert.Equal("Ctrl+Alt+D", settings.KeyBindings["pane.splitRight"]);
        Assert.False(settings.KeyBindings.ContainsKey("tab.new"));
        Assert.True(store.LoadNotes.Count >= 3);
    }

    [Fact]
    public void Settings_WithRemovedRestoreOption_StillLoadCleanly()
    {
        // "restoreLastWorkspace" existed in early builds; ShellFlow now always starts with one terminal.
        File.WriteAllText(Paths.SettingsFile, "{ \"schemaVersion\": 1, \"behavior\": { \"restoreLastWorkspace\": true, \"confirmCloseRunning\": false } }");
        var store = new SettingsStore(Paths);

        var settings = store.Load();

        Assert.Empty(store.LoadNotes);
        Assert.False(settings.Behavior.ConfirmCloseRunning);
    }

    [Fact]
    public void Settings_FromNewerVersionAreNotOverwritten()
    {
        File.WriteAllText(Paths.SettingsFile, "{ \"schemaVersion\": 99, \"terminal\": { \"fontSize\": 15 }, \"futureOption\": true }");
        var store = new SettingsStore(Paths);

        var settings = store.Load();
        settings.Terminal.FontSize = 20;
        var saved = store.Save(settings);

        Assert.Equal(15, new SettingsStore(Paths).Load().Terminal.FontSize);
        Assert.False(saved);
        Assert.Contains("futureOption", File.ReadAllText(Paths.SettingsFile), StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_SaveIsAtomicAndRoundTrips()
    {
        var store = new SettingsStore(Paths);
        var settings = store.Load();
        settings.Appearance.Theme = ThemePreference.Light;
        settings.Appearance.Accent = AccentColor.Mint;
        settings.Terminal.CursorStyle = CursorStyle.Block;

        Assert.True(store.Save(settings));

        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
        var reloaded = new SettingsStore(Paths).Load();
        Assert.Equal(ThemePreference.Light, reloaded.Appearance.Theme);
        Assert.Equal(AccentColor.Mint, reloaded.Appearance.Accent);
        Assert.Equal(CursorStyle.Block, reloaded.Terminal.CursorStyle);
        Assert.Contains("\"theme\": \"Light\"", File.ReadAllText(Paths.SettingsFile), StringComparison.Ordinal);
    }

    [Fact]
    public void ExampleSettingsFile_LoadsWithoutCorrections()
    {
        var example = Path.Combine(FindRepoRoot(), "docs", "settings.example.json");
        File.Copy(example, Paths.SettingsFile);
        var store = new SettingsStore(Paths);

        var settings = store.Load();

        Assert.Empty(store.LoadNotes);
        Assert.Equal("pwsh-dev", Assert.Single(settings.Profiles).Id);
        Assert.Equal("Ctrl+Alt+D", settings.KeyBindings["pane.splitRight"]);
        Assert.Equal("Ctrl+Shift+V | Shift+Insert", settings.KeyBindings["terminal.paste"]);
        Assert.Equal(MotionStyle.Fluid, settings.Appearance.MotionStyle);
    }

    [Fact]
    public void MotionStyle_DefaultsToFluidAndRoundTrips()
    {
        Assert.Equal(MotionStyle.Fluid, new AppSettings().Appearance.MotionStyle);
        File.WriteAllText(Paths.SettingsFile, "{ \"appearance\": { \"motionStyle\": \"Subtle\" } }");
        Assert.Equal(MotionStyle.Subtle, new SettingsStore(Paths).Load().Appearance.MotionStyle);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ShellFlow.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    [Fact]
    public void WorkspaceFile_RejectsPathTraversal()
    {
        Assert.Throws<ArgumentException>(() => Paths.WorkspaceFile(@"..\..\evil"));
        Assert.Throws<ArgumentException>(() => Paths.WorkspaceFile("a/b"));
    }

    [Fact]
    public void State_RoundTrips()
    {
        var store = new WorkspaceStore(Paths);
        store.SaveState(new AppState { LastWorkspaceId = "abc123", Window = new WindowPlacement { X = 10, Y = 20, Width = 1200, Height = 800, Maximized = true } });

        var state = store.LoadState();

        Assert.Equal("abc123", state.LastWorkspaceId);
        Assert.True(state.Window!.Maximized);
        Assert.Equal(1200, state.Window.Width);
    }
}
