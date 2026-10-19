using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using ShellFlow.App.Services;
using ShellFlow.Core.Bridge;
using ShellFlow.Core.Commands;
using ShellFlow.Core.Layout;
using ShellFlow.Core.Profiles;
using ShellFlow.Terminal;
using ShellFlow.TerminalHost;

namespace ShellFlow.App.Workspace;

public enum PaneState
{
    Starting,
    Running,
    Exited,
    Failed,
}

/// <summary>One terminal pane: a renderer, the shell session behind it, and its lifecycle (start, exit, restart, close).</summary>
public sealed class PaneController
{
    private readonly AppServices _services;
    private readonly DispatcherQueue _dispatcher;
    private bool _active;
    private bool _closed;
    private bool _starting;

    public PaneController(PaneLeaf leaf, AppServices services)
    {
        Leaf = leaf;
        _services = services;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Profile = services.Profiles.Find(leaf.ProfileId) ?? services.Profiles.Default;
        leaf.ProfileId = Profile.Id;

        View = new TerminalView();
        View.SetBackground(services.Theme.TerminalBackground);
        Container = new Border
        {
            CornerRadius = new CornerRadius(9),
            // Constant thickness: changing it on focus would resize the terminal every time focus moves.
            BorderThickness = new Thickness(2),
            Padding = new Thickness(1),
            Child = View,
            Opacity = 0,
        };
        AutomationProperties.SetName(Container, $"{Profile.Name} terminal");
        ApplyChrome();
        Container.ActualThemeChanged += (_, _) => ApplyChrome();

        View.RendererReady += OnRendererReady;
        View.TerminalFocused += (_, _) => Activated?.Invoke(this, EventArgs.Empty);
        View.CommandRequested += (_, id) => CommandRequested?.Invoke(this, id);
        View.CopyRequested += (_, text) => CopyRequested?.Invoke(this, text);
        View.SearchResultsChanged += (_, e) => SearchResultsChanged?.Invoke(this, e);
        View.TitleChanged += (_, title) =>
        {
            ShellTitle = title;
            Changed?.Invoke(this, EventArgs.Empty);
        };
        View.WorkingDirectoryChanged += (_, path) =>
        {
            WorkingDirectory = path;
            Changed?.Invoke(this, EventArgs.Empty);
        };
        View.Resized += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        View.InputWithoutSession += OnInputWithoutSession;
        View.DirectoryListRequested += (_, request) => _ = ListFoldersAsync(request);
        View.DirectoryAccepted += (_, accept) => AcceptFolder(accept);
        View.RendererFailed += (_, message) => Fail(message);
    }

    public event EventHandler? Changed;

    public event EventHandler? Activated;

    /// <summary>The shell exited cleanly and the pane should close (when that behavior is enabled).</summary>
    public event EventHandler? CloseRequested;

    public event EventHandler<string>? CommandRequested;

    public event EventHandler<string>? CopyRequested;

    public event EventHandler<SearchResultEventArgs>? SearchResultsChanged;

    public PaneLeaf Leaf { get; }

    public string Id => Leaf.Id;

    public ShellProfile Profile { get; private set; }

    public TerminalView View { get; }

    public Border Container { get; }

    public TerminalSession? Session { get; private set; }

    public PaneState State { get; private set; } = PaneState.Starting;

    public int? ExitCode { get; private set; }

    public string? ErrorMessage { get; private set; }

    /// <summary>Title set by the shell through OSC 0/2, if any.</summary>
    public string? ShellTitle { get; private set; }

    /// <summary>Directory reported by the shell (OSC 7 / OSC 9;9). Null when the shell does not report it.</summary>
    public string? WorkingDirectory { get; private set; }

    /// <summary>Directory the current session was started in.</summary>
    public string? StartedIn { get; private set; }

    public bool HasRunningCommand => Session?.HasRunningCommand ?? false;

    /// <summary>Which shell family this pane runs (decides integration and folder completion).</summary>
    public ShellKind Kind { get; private set; }

    /// <summary>
    /// Folder new panes split from this one should start in: the reported folder when known and usable on Windows,
    /// otherwise the folder this shell started in.
    /// </summary>
    public string? CurrentFolder =>
        WorkingDirectory is { Length: >= 2 } cwd && (cwd[1] == ':' || cwd.StartsWith(@"\\", StringComparison.Ordinal)) && Directory.Exists(cwd)
            ? cwd
            : StartedIn;

    /// <summary>A short label for tabs: the shell's title unless it is just the executable path.</summary>
    public string DisplayTitle
    {
        get
        {
            var title = ShellTitle?.Trim();
            if (string.IsNullOrEmpty(title) || title.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || title.StartsWith("Administrator: C:\\", StringComparison.OrdinalIgnoreCase))
            {
                return Profile.Name;
            }

            return title;
        }
    }

    public bool IsActive
    {
        get => _active;
        set
        {
            _active = value;
            ApplyChrome();
        }
    }

    public async Task InitializeAsync()
    {
        View.Configure(_services.BuildRendererOptions());
        await View.InitializeAsync();
    }

    /// <summary>Shows the pane once it is part of the layout: a Hyprland-style pop-in, a short fade, or instantly.</summary>
    public void Reveal(bool animate, bool fluid)
    {
        if (fluid)
        {
            Motion.PopIn(Container);
            return;
        }

        Container.OpacityTransition = animate ? new ScalarTransition { Duration = TimeSpan.FromMilliseconds(180) } : null;
        Container.Opacity = 1;
    }

    public void ApplyRendererOptions(RendererOptions options)
    {
        View.SetBackground(_services.Theme.TerminalBackground);
        View.Configure(options);
        UpdateFolderCompletion();
        ApplyChrome();
    }

    private void UpdateFolderCompletion() =>
        View.SetFolderCompletion(_services.Settings.Behavior.FolderCompletion && ShellIntegration.CompletionStyle(Kind) is not null && Session is not null);

    private async Task ListFoldersAsync(Core.Bridge.DirectoryListRequest request)
    {
        var cwd = WorkingDirectory is { } reported && reported.Length >= 2 && (reported[1] == ':' || reported.StartsWith(@"\\", StringComparison.Ordinal)) ? reported : null;
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);

        var work = Task.Run(() =>
        {
            if (request.Partial is { } partial)
            {
                var (folder, prefix, error) = FolderCompletion.Resolve(partial, cwd, home);
                return folder is null ? FolderListing.Failed(error ?? "invalid path") : FolderCompletion.List(folder, prefix);
            }

            var (target, showDrives) = FolderCompletion.Navigate(request.Directory!);
            return showDrives ? FolderCompletion.Drives() : target is null ? FolderListing.Failed("invalid path") : FolderCompletion.List(target);
        });

        // Network folders can hang; never leave the picker waiting forever.
        var listing = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(2))) == work ? work.Result : FolderListing.Failed("timed out");
        View.SendDirectoryListing(new Core.Bridge.DirectoryListing(request.Id, listing.Base, listing.Prefix, listing.Entries, listing.IsDriveList, listing.Error));
    }

    private void AcceptFolder(Core.Bridge.DirectoryAcceptMessage accept)
    {
        var (folder, _) = FolderCompletion.Navigate(accept.Directory);
        if (Session is not { State: TerminalSessionState.Running } session || folder is null || !Directory.Exists(folder))
        {
            return;
        }

        // Erase what the user had typed after "cd", then type the quoted folder in this shell's syntax.
        session.Write(new string('\x7f', accept.Erase) + FolderCompletion.Format(folder, Kind) + (accept.Run ? "\r" : string.Empty));
    }

    public void Focus() => View.FocusTerminal();

    public async Task RestartAsync()
    {
        if (_closed)
        {
            return;
        }

        var old = Session;
        Session = null;
        View.Detach();
        if (old is not null)
        {
            await old.CloseAsync(TimeSpan.FromSeconds(2));
        }

        await StartSessionAsync(View.Columns, View.Rows);
    }

    /// <summary>Changes the profile and restarts the shell (used when restoring a missing profile or switching shells).</summary>
    public Task SwitchProfileAsync(ShellProfile profile)
    {
        Profile = profile;
        Leaf.ProfileId = profile.Id;
        return RestartAsync();
    }

    public async Task CloseAsync()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        var session = Session;
        Session = null;
        View.Close();
        if (session is not null)
        {
            await session.CloseAsync(TimeSpan.FromSeconds(2));
        }
    }

    private void OnRendererReady(object? sender, TerminalSizeEventArgs e)
    {
        // Start the shell only once the renderer knows its grid, so the shell's first prompt is laid out
        // for the real size. A renderer restart (crash recovery) re-raises Ready; the session continues.
        if (Session is null && State == PaneState.Starting && !_starting)
        {
            _ = StartSessionAsync(e.Columns, e.Rows);
        }
    }

    private async Task StartSessionAsync(int columns, int rows)
    {
        if (_closed || _starting)
        {
            return;
        }

        _starting = true;
        try
        {
            State = PaneState.Starting;
            ExitCode = null;
            ErrorMessage = null;
            Changed?.Invoke(this, EventArgs.Empty);

            var resolved = _services.Resolve(Profile, Leaf.StartingDirectory);
            if (!resolved.CanLaunch)
            {
                Fail(string.Join(" ", resolved.Issues.Where(i => i.IsError).Select(i => i.Message)));
                return;
            }

            foreach (var issue in resolved.Issues.Where(i => !i.IsError))
            {
                Log.Warn($"Profile '{Profile.Id}': {issue.Message}");
            }

            Kind = ShellIntegration.Detect(resolved.ExecutablePath);
            var launch = _services.Settings.Behavior.ShellIntegration
                ? ShellIntegration.Apply(Kind, Profile.Arguments, Profile.Environment, System.Environment.GetEnvironmentVariable("PROMPT"))
                : new IntegratedLaunch(Profile.Arguments, Profile.Environment, false);

            var options = new TerminalLaunchOptions
            {
                Executable = resolved.ExecutablePath!,
                Arguments = launch.Arguments,
                WorkingDirectory = resolved.StartingDirectory,
                Environment = launch.Environment,
                Columns = columns > 0 ? columns : 120,
                Rows = rows > 0 ? rows : 30,
            };

            TerminalSession session;
            try
            {
                session = await Task.Run(() => TerminalSession.Start(options));
            }
            catch (TerminalStartException ex)
            {
                Log.Error($"Could not start profile '{Profile.Id}'", ex);
                Fail(ex.Message);
                return;
            }

            if (_closed)
            {
                await session.CloseAsync(TimeSpan.FromSeconds(1));
                return;
            }

            Session = session;
            StartedIn = resolved.StartingDirectory;
            WorkingDirectory = null;
            View.Attach(session);
            View.ShowStatus("none", null);
            State = PaneState.Running;
            UpdateFolderCompletion();
            Log.Info($"Started '{Profile.Id}' (pid {session.ProcessId}) at {columns}x{rows}.");
            Changed?.Invoke(this, EventArgs.Empty);

            _ = session.Exited.ContinueWith(t => _dispatcher.TryEnqueue(() => OnSessionExited(session, t.Result)), TaskScheduler.Default);
        }
        finally
        {
            _starting = false;
        }
    }

    private void OnSessionExited(TerminalSession session, int code)
    {
        if (!ReferenceEquals(session, Session) || _closed)
        {
            return;
        }

        State = PaneState.Exited;
        ExitCode = code;
        Log.Info($"Shell '{Profile.Id}' (pid {session.ProcessId}) exited with code {code}.");
        Changed?.Invoke(this, EventArgs.Empty);

        if (code == 0 && _services.Settings.Behavior.CloseOnCleanExit)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        View.ShowStatus("exited", $"Process exited with code {code}  ·  Enter to restart  ·  {Shortcut(CommandIds.ClosePane)} to close");
    }

    private void Fail(string message)
    {
        State = PaneState.Failed;
        ErrorMessage = message;
        Changed?.Invoke(this, EventArgs.Empty);
        View.ShowStatus("error", $"{message}  ·  Enter to retry");
    }

    private void OnInputWithoutSession(object? sender, string data)
    {
        if (State is PaneState.Exited or PaneState.Failed && data.Contains('\r', StringComparison.Ordinal))
        {
            _ = RestartAsync();
        }
    }

    private string Shortcut(string commandId) => _services.Commands.GetShortcut(commandId)?.ToString() ?? "close";

    private void ApplyChrome()
    {
        var theme = Container.ActualTheme;
        Container.Background = ThemeResources.Brush("SfTerminalBackgroundBrush", theme);
        if (_active && _services.Theme.FluidMotion)
        {
            // Hyprland-style active border: a diagonal gradient between two neighboring accents.
            var gradient = new Microsoft.UI.Xaml.Media.LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 1),
            };
            gradient.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop { Color = _services.Theme.AccentColorValue, Offset = 0 });
            gradient.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop { Color = _services.Theme.SecondaryAccentColor, Offset = 1 });
            Container.BorderBrush = gradient;
        }
        else
        {
            Container.BorderBrush = ThemeResources.Brush(_active ? "SfAccentBorderBrush" : "SfBorderSubtleBrush", theme);
        }
    }
}
