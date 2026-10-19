namespace ShellFlow.Core;

/// <summary>What a launch should do when ShellFlow is already running.</summary>
public enum LaunchMode
{
    /// <summary>Bring the existing window forward; open a tab only if a folder or shell was requested.</summary>
    Default,
    NewTab,

    /// <summary>Add a pane to the current tab.</summary>
    NewPane,
}

/// <summary>
/// Command-line options. Used by the Explorer "Open ShellFlow here" entries and by people launching ShellFlow from
/// Run (Win+R) or another shell.
/// <code>
/// ShellFlow.exe [--new-tab | --new-pane] [-d|--directory &lt;path&gt;] [-p|--profile &lt;id&gt;] [&lt;path&gt;]
/// </code>
/// </summary>
public sealed record StartupArguments(string? Directory, string? ProfileId, IReadOnlyList<string> Warnings, LaunchMode Mode = LaunchMode.Default)
{
    private static readonly string[] ScriptExtensions = [".bat", ".cmd"];

    public static StartupArguments Empty { get; } = new(null, null, []);

    /// <summary>A .bat or .cmd file to run in a new tab (from double-click or "Run in ShellFlow").</summary>
    public string? Script { get; init; }

    /// <summary>True when a running instance should open a new terminal for this request.</summary>
    public bool RequestsTerminal => Mode != LaunchMode.Default || Directory is not null || ProfileId is not null || Script is not null;

    public static bool IsScript(string? path) =>
        path is not null && ScriptExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <param name="args">Arguments without the executable path.</param>
    public static StartupArguments Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? directory = null;
        string? profile = null;
        string? script = null;
        var mode = LaunchMode.Default;
        var warnings = new List<string>();

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "-d" or "--directory" or "/d":
                    directory = Next(args, ref i, arg, warnings) ?? directory;
                    break;
                case "-p" or "--profile" or "/p":
                    profile = Next(args, ref i, arg, warnings) ?? profile;
                    break;
                case "--new-tab":
                    mode = LaunchMode.NewTab;
                    break;
                case "--new-pane":
                    mode = LaunchMode.NewPane;
                    break;
                case "--run":
                    var file = Next(args, ref i, arg, warnings)?.Trim().Trim('"');
                    if (file is not null && !IsScript(file))
                    {
                        warnings.Add($"Only .bat and .cmd files can be run with --run (got \"{Path.GetFileName(file)}\").");
                    }
                    else
                    {
                        script = file ?? script;
                    }

                    break;
                default:
                    if (arg.StartsWith('-') && arg.Length > 1)
                    {
                        warnings.Add($"Unknown option \"{arg}\" was ignored.");
                    }
                    else if (IsScript(arg.Trim('"')))
                    {
                        script ??= arg.Trim('"'); // ShellFlow.exe C:\tools\build.bat
                    }
                    else
                    {
                        directory ??= arg; // A bare path, e.g. ShellFlow.exe C:\src
                    }

                    break;
            }
        }

        return new StartupArguments(NormalizeDirectory(directory), string.IsNullOrWhiteSpace(profile) ? null : profile.Trim(), warnings, mode)
        {
            Script = string.IsNullOrWhiteSpace(script) ? null : script,
        };
    }

    /// <summary>
    /// Explorer passes "%V", which for a drive root is "C:\" and is sometimes quoted oddly ("C:\"" → C:"). Repair it.
    /// </summary>
    internal static string? NormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.Trim().Trim('"');
        if (trimmed.Length == 2 && char.IsAsciiLetter(trimmed[0]) && trimmed[1] == ':')
        {
            trimmed += "\\";
        }

        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string? Next(IReadOnlyList<string> args, ref int i, string option, List<string> warnings)
    {
        if (i + 1 >= args.Count)
        {
            warnings.Add($"Option \"{option}\" needs a value.");
            return null;
        }

        i++;
        return args[i];
    }
}
