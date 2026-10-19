using Microsoft.UI.Dispatching;
using ShellFlow.Core.Bridge;
using ShellFlow.Core.Commands;
using ShellFlow.Core.Persistence;
using ShellFlow.Core.Profiles;
using ShellFlow.Core.Settings;

namespace ShellFlow.App.Services;

/// <summary>Application-wide state: settings, profiles, persistence, theme and the command registry.</summary>
public sealed class AppServices
{
    private DispatcherQueueTimer? _saveTimer;

    private AppServices(AppPaths paths, Core.StartupArguments startup)
    {
        Paths = paths;
        Startup = startup;
        SettingsStore = new SettingsStore(paths);
        Workspaces = new WorkspaceStore(paths);
        Settings = SettingsStore.Load();
        Profiles = new ProfileCatalog(SystemShellEnvironment.Instance, Settings);
        var conflicts = Commands.ApplyOverrides(Settings.KeyBindings);
        StartupNotes = [.. SettingsStore.LoadNotes, .. conflicts, .. startup.Warnings];
    }

    /// <summary>Command-line options for this launch (e.g. the folder from "Open ShellFlow here").</summary>
    public Core.StartupArguments Startup { get; }

    public event EventHandler? SettingsChanged;

    public AppPaths Paths { get; }

    public SettingsStore SettingsStore { get; }

    public WorkspaceStore Workspaces { get; }

    public AppSettings Settings { get; }

    public ProfileCatalog Profiles { get; }

    public ThemeService Theme { get; } = new();

    public CommandRegistry Commands { get; } = new();

    /// <summary>Problems found while loading configuration, shown once in the UI.</summary>
    public IReadOnlyList<string> StartupNotes { get; }

    public static AppServices Create(AppPaths paths, Core.StartupArguments? startup = null) => new(paths, startup ?? Core.StartupArguments.Empty);

    /// <summary>Applies a change, validates it, notifies listeners and saves shortly after.</summary>
    public void UpdateSettings(Action<AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        change(Settings);
        SettingsValidator.Normalize(Settings);
        Commands.ApplyOverrides(Settings.KeyBindings);
        Profiles.UpdateSettings(Settings);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    public void SaveSettingsNow()
    {
        _saveTimer?.Stop();
        try
        {
            if (!SettingsStore.Save(Settings))
            {
                Log.Warn("Settings were not saved because settings.json belongs to a newer ShellFlow version.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Could not save settings", ex);
        }
    }

    public RendererOptions BuildRendererOptions()
    {
        var terminal = Settings.Terminal;
        var bindings = Commands.ShortcutMap
            .Select(pair => new RendererKeyBinding(pair.Key.Code, pair.Key.Ctrl, pair.Key.Alt, pair.Key.Shift, pair.Value))
            .ToList();

        return new RendererOptions
        {
            FontFamily = terminal.FontFamily,
            FontSize = terminal.FontSize,
            LineHeight = terminal.LineHeight,
            CursorStyle = terminal.CursorStyle switch
            {
                CursorStyle.Block => "block",
                CursorStyle.Underline => "underline",
                _ => "bar",
            },
            CursorBlink = terminal.CursorBlink,
            Scrollback = terminal.Scrollback,
            Gpu = terminal.GpuAcceleration,
            CopyOnSelect = terminal.CopyOnSelect,
            Theme = Theme.TerminalTheme,
            KeyBindings = bindings,
        };
    }

    /// <summary>Resolves where a new shell should start and with which executable.</summary>
    public ResolvedProfile Resolve(ShellProfile profile, string? paneDirectory) =>
        ProfileResolver.Resolve(
            profile,
            Profiles.Environment,
            paneDirectory,
            Settings.DefaultStartingDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    private void ScheduleSave()
    {
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        if (dispatcher is null)
        {
            SaveSettingsNow();
            return;
        }

        if (_saveTimer is null)
        {
            _saveTimer = dispatcher.CreateTimer();
            _saveTimer.Interval = TimeSpan.FromMilliseconds(400);
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += (_, _) => SaveSettingsNow();
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }
}
