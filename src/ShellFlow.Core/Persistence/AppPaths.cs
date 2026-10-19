namespace ShellFlow.Core.Persistence;

/// <summary>Locations of ShellFlow's files. Everything lives under %LOCALAPPDATA%\ShellFlow unless overridden.</summary>
public sealed class AppPaths
{
    public AppPaths(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    /// <summary>Default location; SHELLFLOW_DATA_DIR overrides it (useful for portable installs and tests).</summary>
    public static AppPaths Default
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("SHELLFLOW_DATA_DIR");
            return new AppPaths(!string.IsNullOrWhiteSpace(overridden) && Path.IsPathFullyQualified(overridden)
                ? overridden
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShellFlow"));
        }
    }

    public string Root { get; }

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string StateFile => Path.Combine(Root, "state.json");

    public string WorkspacesDirectory => Path.Combine(Root, "workspaces");

    public string WebViewDataDirectory => Path.Combine(Root, "WebView2");

    public string LogsDirectory => Path.Combine(Root, "logs");

    public string WorkspaceFile(string id)
    {
        if (string.IsNullOrEmpty(id) || !id.All(char.IsAsciiLetterOrDigit) || id.Length > 64)
        {
            throw new ArgumentException("Workspace ids are alphanumeric.", nameof(id));
        }

        return Path.Combine(WorkspacesDirectory, id + ".json");
    }
}
