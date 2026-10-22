using ShellFlow.Core.Commands;
using ShellFlow.Core.Profiles;

namespace ShellFlow.Core.Settings;

/// <summary>Brings any deserialized settings object into a valid state and reports what was corrected.</summary>
public static class SettingsValidator
{
    public static IReadOnlyList<string> Normalize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var notes = new List<string>();

        settings.Terminal ??= new TerminalSettings();
        settings.Appearance ??= new AppearanceSettings();
        settings.Behavior ??= new BehaviorSettings();
        settings.Profiles ??= [];
        settings.KeyBindings ??= new Dictionary<string, string>(StringComparer.Ordinal);
        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;

        var terminal = settings.Terminal;
        var font = SanitizeFontFamily(terminal.FontFamily);
        if (font != terminal.FontFamily)
        {
            notes.Add("The terminal font family contained unsupported characters and was reset.");
            terminal.FontFamily = font;
        }

        terminal.FontSize = ClampWithNote(terminal.FontSize, TerminalSettings.MinFontSize, TerminalSettings.MaxFontSize, 13, "Font size", notes);
        terminal.LineHeight = ClampWithNote(terminal.LineHeight, 1.0, 2.0, 1.2, "Line height", notes);
        if (terminal.Scrollback is < 0 or > TerminalSettings.MaxScrollback)
        {
            notes.Add($"Scrollback must be between 0 and {TerminalSettings.MaxScrollback:N0} lines.");
            terminal.Scrollback = Math.Clamp(terminal.Scrollback, 0, TerminalSettings.MaxScrollback);
        }

        if (!Enum.IsDefined(terminal.CursorStyle))
        {
            terminal.CursorStyle = CursorStyle.Bar;
        }

        if (!Enum.IsDefined(settings.Appearance.Theme))
        {
            settings.Appearance.Theme = ThemePreference.Dark;
        }

        if (!Enum.IsDefined(settings.Appearance.Accent))
        {
            settings.Appearance.Accent = AccentColor.Periwinkle;
        }

        if (!Enum.IsDefined(settings.Appearance.Animations))
        {
            settings.Appearance.Animations = AnimationPreference.System;
        }

        if (!Enum.IsDefined(settings.Appearance.MotionStyle))
        {
            settings.Appearance.MotionStyle = MotionStyle.Fluid;
        }

        if (!Enum.IsDefined(settings.Appearance.TabPosition))
        {
            settings.Appearance.TabPosition = TabPosition.Left;
        }

        if (settings.DefaultStartingDirectory is { } dir && (string.IsNullOrWhiteSpace(dir) || dir.Length > 1024))
        {
            settings.DefaultStartingDirectory = null;
        }

        NormalizeProfiles(settings, notes);
        NormalizeKeyBindings(settings, notes);
        return notes;
    }

    public static string SanitizeFontFamily(string? value)
    {
        const string fallback = "Cascadia Mono, Cascadia Code, Consolas, monospace";
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200)
        {
            return fallback;
        }

        // The value is handed to the renderer as a CSS font-family list. Only allow characters that can appear in font names.
        return value.All(c => char.IsLetterOrDigit(c) || c is ' ' or ',' or '-' or '_' or '\'' or '"' or '.')
            ? value.Trim()
            : fallback;
    }

    private static double ClampWithNote(double value, double min, double max, double fallback, string label, List<string> notes)
    {
        if (!double.IsFinite(value))
        {
            notes.Add($"{label} was invalid and was reset.");
            return fallback;
        }

        if (value < min || value > max)
        {
            notes.Add($"{label} must be between {min} and {max}.");
            return Math.Clamp(value, min, max);
        }

        return value;
    }

    private static void NormalizeProfiles(AppSettings settings, List<string> notes)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var valid = new List<ShellProfile>();
        foreach (var profile in settings.Profiles)
        {
            if (profile is null)
            {
                continue;
            }

            profile.Source = ProfileSource.User;
            if (!ProfileResolver.IsValidId(profile.Id))
            {
                notes.Add($"Profile \"{profile.Name}\" has an invalid id (use lowercase letters, digits, '.', '_' or '-') and was skipped.");
                continue;
            }

            if (!seen.Add(profile.Id))
            {
                notes.Add($"Duplicate profile id \"{profile.Id}\" was skipped.");
                continue;
            }

            profile.Name = string.IsNullOrWhiteSpace(profile.Name) ? profile.Id : profile.Name.Trim();
            if (profile.Name.Length > 64)
            {
                profile.Name = profile.Name[..64];
            }

            if (profile.Accent is not null && !ProfileResolver.IsValidAccent(profile.Accent))
            {
                profile.Accent = null;
            }

            valid.Add(profile);
        }

        settings.Profiles = valid;
        if (settings.DefaultProfileId is not null && !ProfileResolver.IsValidId(settings.DefaultProfileId))
        {
            settings.DefaultProfileId = null;
        }
    }

    private static void NormalizeKeyBindings(AppSettings settings, List<string> notes)
    {
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (command, chord) in settings.KeyBindings)
        {
            if (string.IsNullOrWhiteSpace(command) || command.Length > 64)
            {
                continue;
            }

            if (string.IsNullOrEmpty(chord))
            {
                normalized[command] = string.Empty;
            }
            else if (CommandRegistry.ParseChords(chord) is { Count: > 0 } parsed)
            {
                normalized[command] = string.Join(" | ", parsed);
            }
            else
            {
                notes.Add($"Shortcut \"{chord}\" for \"{command}\" is not valid and was ignored.");
            }
        }

        settings.KeyBindings = normalized;
    }
}
