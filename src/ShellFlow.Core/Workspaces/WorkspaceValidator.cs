using ShellFlow.Core.Layout;

namespace ShellFlow.Core.Workspaces;

public static class WorkspaceValidator
{
    public const int MaxNameLength = 64;
    public const int MaxTabs = 64;
    public const int MaxPanesPerTab = 32;

    /// <summary>
    /// Repairs a deserialized workspace so it can always be opened: missing roots, unknown profiles, duplicate ids,
    /// invalid ratios and out-of-range selections are corrected. Returns human-readable notes about the repairs.
    /// </summary>
    public static IReadOnlyList<string> Normalize(WorkspaceDocument workspace, Func<string, bool> profileExists, string defaultProfileId)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(profileExists);
        var notes = new List<string>();

        workspace.SchemaVersion = WorkspaceDocument.CurrentSchemaVersion;
        workspace.Name = NormalizeName(workspace.Name);
        if (string.IsNullOrWhiteSpace(workspace.Id) || workspace.Id.Length > 64 || !workspace.Id.All(char.IsAsciiLetterOrDigit))
        {
            workspace.Id = WorkspaceDocument.NewId();
        }

        workspace.Tabs = (workspace.Tabs ?? []).Where(t => t is not null).Take(MaxTabs).ToList();
        var seenPaneIds = new HashSet<string>(StringComparer.Ordinal);
        var seenTabIds = new HashSet<string>(StringComparer.Ordinal);
        var missingProfiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tab in workspace.Tabs)
        {
            if (string.IsNullOrWhiteSpace(tab.Id) || !seenTabIds.Add(tab.Id))
            {
                tab.Id = PaneLeaf.NewId();
                seenTabIds.Add(tab.Id);
            }

            if (tab.Title is not null)
            {
                tab.Title = string.IsNullOrWhiteSpace(tab.Title) ? null : tab.Title.Trim()[..Math.Min(tab.Title.Trim().Length, MaxNameLength)];
            }

            tab.Root = NormalizeNode(tab.Root, seenPaneIds, profileExists, defaultProfileId, missingProfiles, depth: 0);
            if (tab.Root is null || PaneTree.Count(tab.Root) > MaxPanesPerTab)
            {
                tab.Root = new PaneLeaf { ProfileId = defaultProfileId };
                seenPaneIds.Add(((PaneLeaf)tab.Root).Id);
                notes.Add("A tab with an unreadable layout was reset to a single pane.");
            }

            if (tab.FocusedPaneId is null || PaneTree.FindLeaf(tab.Root, tab.FocusedPaneId) is null)
            {
                tab.FocusedPaneId = PaneTree.Leaves(tab.Root).First().Id;
            }
        }

        foreach (var missing in missingProfiles)
        {
            notes.Add($"Shell profile \"{missing}\" is not available on this machine; those panes use the default shell.");
        }

        if (workspace.Tabs.Count == 0)
        {
            var leaf = new PaneLeaf { ProfileId = defaultProfileId };
            workspace.Tabs.Add(new TabDocument { Root = leaf, FocusedPaneId = leaf.Id });
        }

        workspace.SelectedTabIndex = Math.Clamp(workspace.SelectedTabIndex, 0, workspace.Tabs.Count - 1);
        return notes;
    }

    public static string NormalizeName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        trimmed = new string(trimmed.Where(c => !char.IsControl(c)).ToArray());
        if (trimmed.Length == 0)
        {
            return "Workspace";
        }

        return trimmed.Length > MaxNameLength ? trimmed[..MaxNameLength] : trimmed;
    }

    private static PaneNode? NormalizeNode(PaneNode? node, HashSet<string> seen, Func<string, bool> profileExists, string defaultProfileId, HashSet<string> missing, int depth)
    {
        if (depth > MaxPanesPerTab)
        {
            return null;
        }

        switch (node)
        {
            case PaneLeaf leaf:
                if (string.IsNullOrWhiteSpace(leaf.Id) || leaf.Id.Length > 64 || !seen.Add(leaf.Id))
                {
                    leaf.Id = PaneLeaf.NewId();
                    seen.Add(leaf.Id);
                }

                if (string.IsNullOrWhiteSpace(leaf.ProfileId) || !profileExists(leaf.ProfileId))
                {
                    if (!string.IsNullOrWhiteSpace(leaf.ProfileId))
                    {
                        missing.Add(leaf.ProfileId);
                    }

                    leaf.ProfileId = defaultProfileId;
                }

                if (leaf.StartingDirectory is { Length: > 1024 })
                {
                    leaf.StartingDirectory = null;
                }

                return leaf;

            case PaneSplit split:
                var first = NormalizeNode(split.First, seen, profileExists, defaultProfileId, missing, depth + 1);
                var second = NormalizeNode(split.Second, seen, profileExists, defaultProfileId, missing, depth + 1);
                if (first is null || second is null)
                {
                    return first ?? second;
                }

                split.First = first;
                split.Second = second;
                if (!Enum.IsDefined(split.Arrangement))
                {
                    split.Arrangement = PaneArrangement.SideBySide;
                }

                split.Ratio = double.IsFinite(split.Ratio) ? Math.Clamp(split.Ratio, PaneSplit.MinRatio, PaneSplit.MaxRatio) : 0.5;
                return split;

            default:
                return null;
        }
    }
}
