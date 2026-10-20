using System.Diagnostics;
using ShellFlow.Core.Workspaces;

namespace ShellFlow.Core.Persistence;

public sealed class WorkspaceStore(AppPaths paths)
{
    public AppPaths Paths { get; } = paths;

    public IReadOnlyList<WorkspaceSummary> List()
    {
        if (!Directory.Exists(Paths.WorkspacesDirectory))
        {
            return [];
        }

        var summaries = new List<WorkspaceSummary>();
        foreach (var file in Directory.EnumerateFiles(Paths.WorkspacesDirectory, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (!id.All(char.IsAsciiLetterOrDigit))
            {
                continue; // Skips backups such as "x.corrupt-...json".
            }

            var result = JsonFileStore.Load(file, ShellFlowJsonContext.Default.WorkspaceDocument, WorkspaceDocument.CurrentSchemaVersion, () => new WorkspaceDocument { Id = id });
            if (result.Status is LoadStatus.Loaded or LoadStatus.NewerVersion)
            {
                summaries.Add(new WorkspaceSummary(id, WorkspaceValidator.NormalizeName(result.Value.Name), result.Value.LastSaved, result.Value.Tabs?.Count ?? 0));
            }
        }

        return summaries.OrderByDescending(s => s.LastSaved ?? DateTimeOffset.MinValue).ToList();
    }

    public LoadResult<WorkspaceDocument>? Load(string id)
    {
        var path = Paths.WorkspaceFile(id);
        if (!File.Exists(path))
        {
            return null;
        }

        var result = JsonFileStore.Load(path, ShellFlowJsonContext.Default.WorkspaceDocument, WorkspaceDocument.CurrentSchemaVersion, () => new WorkspaceDocument { Id = id });
        result.Value.Id = id; // The file name is authoritative.
        return result;
    }

    public void Save(WorkspaceDocument workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        workspace.SchemaVersion = WorkspaceDocument.CurrentSchemaVersion;
        workspace.LastSaved = DateTimeOffset.Now;
        JsonFileStore.Save(Paths.WorkspaceFile(workspace.Id), workspace, ShellFlowJsonContext.Default.WorkspaceDocument);
    }

    public bool Delete(string id)
    {
        var path = Paths.WorkspaceFile(id);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    public AppState LoadState()
    {
        var result = JsonFileStore.Load(Paths.StateFile, ShellFlowJsonContext.Default.AppState, 1, () => new AppState());
        if (result.Status == LoadStatus.Recovered)
        {
            Trace.TraceWarning("ShellFlow: state.json was reset.");
        }

        return result.Value;
    }

    public void SaveState(AppState state) => JsonFileStore.Save(Paths.StateFile, state, ShellFlowJsonContext.Default.AppState);
}
