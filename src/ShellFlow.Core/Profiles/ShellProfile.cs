using System.Text.Json.Serialization;

namespace ShellFlow.Core.Profiles;

public enum ProfileSource
{
    /// <summary>Found on this machine at startup (PowerShell, CMD, Git Bash, WSL). Not persisted.</summary>
    Detected,

    /// <summary>Defined by the user in settings.json.</summary>
    User,
}

/// <summary>Known icon keys. The UI maps them to glyphs; unknown values fall back to <see cref="Terminal"/>.</summary>
public static class ProfileIcons
{
    public const string PowerShell = "powershell";
    public const string Cmd = "cmd";
    public const string Bash = "bash";
    public const string Linux = "linux";
    public const string Terminal = "terminal";
}

public sealed class ShellProfile
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Executable path or a name resolved through PATH. Environment variables are expanded.</summary>
    public string Executable { get; set; } = string.Empty;

    /// <summary>Raw Windows command-line arguments.</summary>
    public string? Arguments { get; set; }

    public string? StartingDirectory { get; set; }

    /// <summary>Variables to add or override for this shell; a null value removes a variable.</summary>
    public Dictionary<string, string?>? Environment { get; set; }

    public string? Icon { get; set; }

    /// <summary>Optional accent as #RRGGBB, used for the tab indicator.</summary>
    public string? Accent { get; set; }

    public bool Hidden { get; set; }

    [JsonIgnore]
    public ProfileSource Source { get; set; } = ProfileSource.User;

    public ShellProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        Executable = Executable,
        Arguments = Arguments,
        StartingDirectory = StartingDirectory,
        Environment = Environment is null ? null : new Dictionary<string, string?>(Environment, StringComparer.OrdinalIgnoreCase),
        Icon = Icon,
        Accent = Accent,
        Hidden = Hidden,
        Source = Source,
    };
}
