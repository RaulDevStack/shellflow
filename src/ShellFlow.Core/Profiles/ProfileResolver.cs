using System.Text.RegularExpressions;

namespace ShellFlow.Core.Profiles;

public sealed record ProfileIssue(string Field, string Message, bool IsError);

/// <summary>A profile resolved against the machine: ready to launch, or carrying actionable errors.</summary>
public sealed record ResolvedProfile(ShellProfile Profile, string? ExecutablePath, string? StartingDirectory, IReadOnlyList<ProfileIssue> Issues)
{
    public bool CanLaunch => ExecutablePath is not null && !Issues.Any(i => i.IsError);
}

public static partial class ProfileResolver
{
    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex AccentPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex IdPattern();

    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    public static bool IsValidAccent(string? accent) => accent is not null && AccentPattern().IsMatch(accent);

    /// <summary>Resolves an executable to a fully qualified existing path, searching PATH and PATHEXT when needed.</summary>
    public static string? ResolveExecutable(string? executable, IShellEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(env);
        if (string.IsNullOrWhiteSpace(executable))
        {
            return null;
        }

        var expanded = env.ExpandVariables(executable.Trim().Trim('"'));
        if (expanded.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return null;
        }

        var hasExtension = Path.HasExtension(expanded);
        var extensions = hasExtension
            ? [string.Empty]
            : (env.GetVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ext => ext.ToLowerInvariant())
                .ToArray();

        if (Path.IsPathFullyQualified(expanded))
        {
            return extensions.Select(ext => expanded + ext).FirstOrDefault(env.FileExists);
        }

        if (expanded.Contains(Path.DirectorySeparatorChar) || expanded.Contains(Path.AltDirectorySeparatorChar))
        {
            return null; // Relative paths are ambiguous for a terminal; require an absolute path or a bare name.
        }

        var searchPath = (env.GetVariable("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var directory in searchPath)
        {
            var dir = env.ExpandVariables(directory.Trim('"'));
            if (!Path.IsPathFullyQualified(dir))
            {
                continue;
            }

            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir, expanded + ext);
                if (env.FileExists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>Validates a profile and resolves its executable and starting directory.</summary>
    /// <param name="fallbackDirectory">Used when neither the pane nor the profile specify a usable directory.</param>
    public static ResolvedProfile Resolve(ShellProfile profile, IShellEnvironment env, string? paneDirectory, string? defaultDirectory, string fallbackDirectory)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(env);
        var issues = new List<ProfileIssue>();

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            issues.Add(new ProfileIssue(nameof(profile.Name), "The profile needs a name.", IsError: false));
        }

        var executable = ResolveExecutable(profile.Executable, env);
        if (executable is null)
        {
            issues.Add(new ProfileIssue(
                nameof(profile.Executable),
                string.IsNullOrWhiteSpace(profile.Executable)
                    ? "No executable is configured for this profile."
                    : $"\"{profile.Executable}\" could not be found. Check the path in Settings, or install the shell.",
                IsError: true));
        }

        if (profile.Accent is not null && !IsValidAccent(profile.Accent))
        {
            issues.Add(new ProfileIssue(nameof(profile.Accent), $"Accent \"{profile.Accent}\" is not a #RRGGBB color and was ignored.", IsError: false));
        }

        string? directory = null;
        foreach (var candidate in new[] { paneDirectory, profile.StartingDirectory, defaultDirectory })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var expanded = env.ExpandVariables(candidate.Trim().Trim('"'));
            if (expanded == "~")
            {
                expanded = fallbackDirectory;
            }

            if (Path.IsPathFullyQualified(expanded) && env.DirectoryExists(expanded))
            {
                directory = expanded;
                break;
            }

            issues.Add(new ProfileIssue(nameof(profile.StartingDirectory), $"Starting directory \"{candidate}\" does not exist; using a fallback.", IsError: false));
        }

        return new ResolvedProfile(profile, executable, directory ?? fallbackDirectory, issues);
    }
}
