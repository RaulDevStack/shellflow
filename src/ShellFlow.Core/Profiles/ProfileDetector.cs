namespace ShellFlow.Core.Profiles;

/// <summary>Finds the shells installed on this machine. Nothing is assumed to exist.</summary>
public static class ProfileDetector
{
    public const string PowerShellCoreId = "pwsh";
    public const string WindowsPowerShellId = "windows-powershell";
    public const string CmdId = "cmd";
    public const string GitBashId = "git-bash";
    public const string WslIdPrefix = "wsl-";

    public static IReadOnlyList<ShellProfile> Detect(IShellEnvironment env, bool includeWsl = true)
    {
        ArgumentNullException.ThrowIfNull(env);
        var profiles = new List<ShellProfile>();

        var pwsh = FirstExisting(env,
            "%ProgramFiles%\\PowerShell\\7\\pwsh.exe",
            "%LOCALAPPDATA%\\Microsoft\\WindowsApps\\pwsh.exe") ?? ProfileResolver.ResolveExecutable("pwsh.exe", env);
        if (pwsh is not null)
        {
            profiles.Add(Detected(PowerShellCoreId, "PowerShell", pwsh, "-NoLogo", ProfileIcons.PowerShell, "#A6B7FF"));
        }

        var windowsPowerShell = FirstExisting(env, "%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe");
        if (windowsPowerShell is not null)
        {
            profiles.Add(Detected(WindowsPowerShellId, "Windows PowerShell", windowsPowerShell, "-NoLogo", ProfileIcons.PowerShell, "#91D8F7"));
        }

        var cmd = FirstExisting(env, "%ComSpec%", "%SystemRoot%\\System32\\cmd.exe");
        if (cmd is not null)
        {
            profiles.Add(Detected(CmdId, "Command Prompt", cmd, null, ProfileIcons.Cmd, "#A0A6B5"));
        }

        var gitBash = FirstExisting(env,
            "%ProgramFiles%\\Git\\bin\\bash.exe",
            "%ProgramFiles(x86)%\\Git\\bin\\bash.exe",
            "%LOCALAPPDATA%\\Programs\\Git\\bin\\bash.exe");
        if (gitBash is not null)
        {
            profiles.Add(Detected(GitBashId, "Git Bash", gitBash, "--login -i", ProfileIcons.Bash, "#E8BFA6"));
        }

        if (includeWsl)
        {
            var wsl = FirstExisting(env, "%SystemRoot%\\System32\\wsl.exe");
            if (wsl is not null)
            {
                foreach (var distro in env.GetWslDistributions())
                {
                    profiles.Add(Detected(WslIdPrefix + Slug(distro), distro, wsl, $"-d \"{distro}\" --cd ~", ProfileIcons.Linux, "#A3DCC5"));
                }
            }
        }

        return profiles;
    }

    /// <summary>Parses <c>wsl --list --quiet</c> output, dropping internal distributions such as Docker's.</summary>
    public static IReadOnlyList<string> ParseWslList(string output)
    {
        return output
            .Replace("\0", string.Empty, StringComparison.Ordinal)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => name.Length is > 0 and <= 128 && !name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase) && IsSafeDistroName(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsSafeDistroName(string name) => name.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ' ');

    private static string Slug(string value) =>
        new string(value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');

    private static string? FirstExisting(IShellEnvironment env, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var expanded = env.ExpandVariables(candidate);
            if (!expanded.Contains('%', StringComparison.Ordinal) && Path.IsPathFullyQualified(expanded) && env.FileExists(expanded))
            {
                return expanded;
            }
        }

        return null;
    }

    private static ShellProfile Detected(string id, string name, string executable, string? arguments, string icon, string accent) => new()
    {
        Id = id,
        Name = name,
        Executable = executable,
        Arguments = arguments,
        Icon = icon,
        Accent = accent,
        Source = ProfileSource.Detected,
    };
}
