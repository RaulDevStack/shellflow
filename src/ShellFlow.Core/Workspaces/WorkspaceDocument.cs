using ShellFlow.Core.Layout;

namespace ShellFlow.Core.Workspaces;

/// <summary>
/// The persisted structure of a workspace: its tabs and split layouts. Running processes are never restored;
/// each pane starts a fresh shell from its profile and starting directory.
/// </summary>
public sealed class WorkspaceDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string Id { get; set; } = NewId();

    public string Name { get; set; } = "Workspace";

    public List<TabDocument> Tabs { get; set; } = [];

    public int SelectedTabIndex { get; set; }

    public DateTimeOffset? LastSaved { get; set; }

    public static string NewId() => Guid.NewGuid().ToString("N")[..16];
}

public sealed class TabDocument
{
    public string Id { get; set; } = PaneLeaf.NewId();

    /// <summary>User-assigned title. Null shows the shell's title or profile name.</summary>
    public string? Title { get; set; }

    public PaneNode? Root { get; set; }

    public string? FocusedPaneId { get; set; }
}

/// <summary>Index entry used by the workspace switcher.</summary>
public sealed record WorkspaceSummary(string Id, string Name, DateTimeOffset? LastSaved, int TabCount);
