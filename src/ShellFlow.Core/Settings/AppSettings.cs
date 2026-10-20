using System.Text.Json.Serialization;
using ShellFlow.Core.Profiles;

namespace ShellFlow.Core.Settings;

[JsonConverter(typeof(JsonStringEnumConverter<ThemePreference>))]
public enum ThemePreference
{
    Dark,
    Light,
    System,
}

[JsonConverter(typeof(JsonStringEnumConverter<AccentColor>))]
public enum AccentColor
{
    Periwinkle,
    Sky,
    Mint,
    Peach,
}

[JsonConverter(typeof(JsonStringEnumConverter<AnimationPreference>))]
public enum AnimationPreference
{
    /// <summary>Follow the Windows "Animation effects" setting.</summary>
    System,
    Full,
    Off,
}

[JsonConverter(typeof(JsonStringEnumConverter<MotionStyle>))]
public enum MotionStyle
{
    /// <summary>Hyprland-inspired: panes pop in, layouts glide with a slight overshoot, tabs slide.</summary>
    Fluid,

    /// <summary>Short fades only.</summary>
    Subtle,
}

[JsonConverter(typeof(JsonStringEnumConverter<CursorStyle>))]
public enum CursorStyle
{
    Block,
    Bar,
    Underline,
}

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Profile used for new tabs and panes. Null picks the best detected PowerShell.</summary>
    public string? DefaultProfileId { get; set; }

    /// <summary>Directory new shells start in when the profile does not specify one. Null means the user profile folder.</summary>
    public string? DefaultStartingDirectory { get; set; }

    public TerminalSettings Terminal { get; set; } = new();

    public AppearanceSettings Appearance { get; set; } = new();

    public BehaviorSettings Behavior { get; set; } = new();

    /// <summary>User-defined shell profiles. Detected profiles are not stored here.</summary>
    public List<ShellProfile> Profiles { get; set; } = [];

    /// <summary>Overrides for default shortcuts: command id → chord (for example "Ctrl+Alt+T"). An empty string unbinds.</summary>
    public Dictionary<string, string> KeyBindings { get; set; } = new(StringComparer.Ordinal);
}

public sealed class TerminalSettings
{
    public const double MinFontSize = 8;
    public const double MaxFontSize = 36;
    public const int MaxScrollback = 100_000;

    public string FontFamily { get; set; } = "Cascadia Mono, Cascadia Code, Consolas, monospace";

    public double FontSize { get; set; } = 13;

    public double LineHeight { get; set; } = 1.2;

    public CursorStyle CursorStyle { get; set; } = CursorStyle.Bar;

    public bool CursorBlink { get; set; } = true;

    public int Scrollback { get; set; } = 10_000;

    /// <summary>Use the WebGL renderer when available; falls back to the DOM renderer automatically.</summary>
    public bool GpuAcceleration { get; set; } = true;

    public bool CopyOnSelect { get; set; }
}

public sealed class AppearanceSettings
{
    public ThemePreference Theme { get; set; } = ThemePreference.Dark;

    public AccentColor Accent { get; set; } = AccentColor.Periwinkle;

    public AnimationPreference Animations { get; set; } = AnimationPreference.System;

    /// <summary>How animations look when they are enabled.</summary>
    public MotionStyle MotionStyle { get; set; } = MotionStyle.Fluid;
}

public sealed class BehaviorSettings
{
    /// <summary>Ask before closing a pane, tab, workspace or the window while a command is running.</summary>
    public bool ConfirmCloseRunning { get; set; } = true;

    /// <summary>Close a pane automatically when its shell exits with code 0 (for example after typing "exit").</summary>
    public bool CloseOnCleanExit { get; set; } = true;

    /// <summary>
    /// Let PowerShell, CMD and Git Bash report their current folder (an invisible OSC 9;9 before each prompt), so new
    /// panes open in the same folder and the status bar shows it.
    /// </summary>
    public bool ShellIntegration { get; set; } = true;

    /// <summary>Typing "cd" and pressing Tab opens a folder picker (needs the shell's current folder).</summary>
    public bool FolderCompletion { get; set; } = true;

    /// <summary>Warn before pasting text that contains line breaks, which shells execute immediately.</summary>
    public bool ConfirmMultilinePaste { get; set; } = true;
}
