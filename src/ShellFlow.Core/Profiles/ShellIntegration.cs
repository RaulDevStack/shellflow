using System.Text;

namespace ShellFlow.Core.Profiles;

public enum ShellKind
{
    Unknown,
    PowerShell,
    Cmd,
    GitBash,
    Wsl,
}

/// <summary>How paths are written on a shell's command line.</summary>
public enum PathStyle
{
    Windows,
    Posix,
}

/// <summary>Launch arguments and environment adjusted for shell integration.</summary>
public sealed record IntegratedLaunch(string? Arguments, IReadOnlyDictionary<string, string?>? Environment, bool Integrated);

/// <summary>
/// Makes shells report their current directory with OSC 9;9 after every command, so ShellFlow can show it, open new
/// panes in the same folder, and offer folder completion. The user's own prompt and profile are kept: ShellFlow only
/// prepends an invisible escape sequence to whatever prompt the shell already prints.
/// </summary>
public static class ShellIntegration
{
    /// <summary>
    /// PowerShell: wraps the existing prompt function (after the user's profile has loaded).
    /// BEL terminates the OSC because it passes through every ConPTY version.
    /// </summary>
    internal const string PowerShellScript =
        "$global:__ShellFlowPrompt = $function:prompt\n" +
        "function global:prompt {\n" +
        "  $loc = $executionContext.SessionState.Path.CurrentLocation\n" +
        "  $osc = ''\n" +
        "  if ($loc.Provider.Name -eq 'FileSystem') { $osc = \"$([char]27)]9;9;`\"$($loc.ProviderPath)`\"$([char]7)\" }\n" +
        "  $osc + (& $global:__ShellFlowPrompt)\n" +
        "}\n";

    /// <summary>cmd.exe: $e]9;9;$P$e\ before the prompt ($P is the current drive and path).</summary>
    internal const string CmdPromptPrefix = "$e]9;9;$P$e\\";

    /// <summary>Git Bash: report the Windows form of $PWD before each prompt.</summary>
    internal const string BashPromptCommand = "printf '\\033]9;9;%s\\007' \"$(cygpath -w \"$PWD\" 2>/dev/null || pwd)\"";

    public static ShellKind Detect(string? executablePath)
    {
        var name = Path.GetFileName(executablePath ?? string.Empty).ToLowerInvariant();
        return name switch
        {
            "powershell.exe" or "pwsh.exe" => ShellKind.PowerShell,
            "cmd.exe" => ShellKind.Cmd,
            "bash.exe" or "git-bash.exe" => ShellKind.GitBash,
            "wsl.exe" => ShellKind.Wsl,
            _ => ShellKind.Unknown,
        };
    }

    /// <summary>Path style for folder completion, or null when completion is not supported for the shell.</summary>
    public static PathStyle? CompletionStyle(ShellKind kind) => kind switch
    {
        ShellKind.PowerShell or ShellKind.Cmd => PathStyle.Windows,
        ShellKind.GitBash => PathStyle.Posix,
        _ => null,
    };

    /// <param name="currentPrompt">The PROMPT variable cmd.exe would otherwise use (null for the default "$P$G").</param>
    public static IntegratedLaunch Apply(ShellKind kind, string? arguments, IReadOnlyDictionary<string, string?>? environment, string? currentPrompt)
    {
        var env = environment is null
            ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string?>(environment, StringComparer.OrdinalIgnoreCase);

        switch (kind)
        {
            case ShellKind.PowerShell when !RunsOwnCommand(arguments):
            {
                var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(PowerShellScript));
                var args = string.IsNullOrWhiteSpace(arguments) ? string.Empty : arguments.Trim() + " ";
                return new IntegratedLaunch($"{args}-NoExit -EncodedCommand {encoded}", env, true);
            }

            case ShellKind.Cmd when !env.ContainsKey("PROMPT"):
                env["PROMPT"] = CmdPromptPrefix + (string.IsNullOrEmpty(currentPrompt) ? "$P$G" : currentPrompt);
                return new IntegratedLaunch(arguments, env, true);

            case ShellKind.GitBash when !env.ContainsKey("PROMPT_COMMAND"):
                env["PROMPT_COMMAND"] = BashPromptCommand;
                return new IntegratedLaunch(arguments, env, true);

            default:
                return new IntegratedLaunch(arguments, environment, false);
        }
    }

    /// <summary>True when the profile already runs a command or script; integration then stays out of the way.</summary>
    private static bool RunsOwnCommand(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return false;
        }

        foreach (var token in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = token.ToLowerInvariant();
            if (t is "-c" or "-command" or "-file" or "-f" or "-encodedcommand" or "-enc" or "-e" or "-ec" or "-noexit")
            {
                return true;
            }
        }

        return false;
    }
}
