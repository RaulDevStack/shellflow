using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using ShellFlow.App.Controls;
using ShellFlow.App.Services;
using ShellFlow.App.Workspace;
using ShellFlow.Core.Commands;
using ShellFlow.Core.Layout;
using ShellFlow.Core.Persistence;
using ShellFlow.Core.Profiles;
using ShellFlow.Core.Settings;
using ShellFlow.Core.Workspaces;
using Windows.ApplicationModel.DataTransfer;

namespace ShellFlow.App;

/// <summary>The window's content: chrome, workspace area and overlays. Every user action routes through the command registry.</summary>
public sealed partial class MainView : UserControl
{
    private readonly AppServices _services;
    private readonly WorkspaceController _workspace;
    private readonly DispatcherQueueTimer _statusTimer;
    private bool _syncingSelection;

    public MainView(AppServices services)
    {
        _services = services;
        InitializeComponent();

        _workspace = new WorkspaceController(services, TabHostContainer);
        TabStrip.ItemsSource = _workspace.Tabs;

        // Tabs share the title bar's free space and scroll when even their minimum width does not fit.
        TitleBar.SizeChanged += (_, _) => UpdateTabSizing();
        NewTabButtons.SizeChanged += (_, _) => UpdateTabSizing();
        _workspace.Tabs.CollectionChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdateTabSizing);
        TabStrip.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnTabStripWheel), handledEventsToo: true);
        ApplyTabPosition();

        // Sliding tabs must not draw over the title bar or status bar.
        TabHostContainer.SizeChanged += (_, e) => TabHostContainer.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height),
        };
        TabStrip.DragItemsCompleted += (_, _) => SyncTabStripSelection();

        _workspace.SelectionChanged += (_, _) => SyncTabStripSelection();
        _workspace.StatusChanged += (_, _) => UpdateStatus();
        _workspace.TabsEmptied += (_, _) => UpdateEmptyState();
        _workspace.Tabs.CollectionChanged += (_, _) => UpdateEmptyState();
        _workspace.PaneCommandRequested += (_, e) => ExecuteFromRenderer(e.CommandId);
        _workspace.CopyRequested += (_, text) => CopyToClipboard(text);
        _workspace.SearchResultsChanged += (_, e) => FindBar.ShowResults(e.Index, e.Count);

        FindBar.SearchRequested += OnSearchRequested;
        FindBar.Closed += (_, _) =>
        {
            _workspace.FocusedPane?.View.ClearFind();
            _workspace.FocusedPane?.Focus();
        };
        Palette.Closed += (_, _) =>
        {
            InteractiveRegionsChanged?.Invoke(this, EventArgs.Empty);
            if (!SettingsPanel.IsOpen)
            {
                _workspace.FocusedPane?.Focus();
            }
        };
        SettingsPanel.Closed += (_, _) => _workspace.FocusedPane?.Focus();

        _services.Commands.CommandFailed += (_, e) =>
        {
            Log.Error($"Command '{e.Command.Id}' failed", e.Exception);
            ShowNotice($"\"{e.Command.Title}\" failed: {e.Exception.Message}", InfoBarSeverity.Error);
        };
        _services.SettingsChanged += (_, _) => OnSettingsChanged();
        _services.Profiles.Changed += (_, _) => DispatcherQueue.TryEnqueue(BuildProfileMenu);
        _services.Theme.ThemeChanged += (_, _) => ApplyRendererOptions();
        _services.Theme.MotionChanged += (_, _) => ApplyMotion();

        PreviewKeyDown += OnPreviewKeyDown;
        TabArea.SizeChanged += (_, _) => InteractiveRegionsChanged?.Invoke(this, EventArgs.Empty);
        Toolbar.SizeChanged += (_, _) => InteractiveRegionsChanged?.Invoke(this, EventArgs.Empty);
        WorkspaceButton.SizeChanged += (_, _) => InteractiveRegionsChanged?.Invoke(this, EventArgs.Empty);

        RegisterCommands();
        SettingsPanel.Initialize(services);

        _statusTimer = DispatcherQueue.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromSeconds(2);
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();
    }

    /// <summary>The title bar's interactive areas moved or changed; the window must update its drag regions.</summary>
    public event EventHandler? InteractiveRegionsChanged;

    public WorkspaceController Workspace => _workspace;

    /// <summary>Elements in the title bar row that must receive clicks instead of dragging the window.</summary>
    public IEnumerable<FrameworkElement> InteractiveTitleBarElements =>
        Palette.IsOpen ? [TitleBar] : TabsInSidePanel ? [WorkspaceButton, Toolbar] : [WorkspaceButton, TabArea, Toolbar];

    private bool TabsInSidePanel => _tabPosition == TabPosition.Left;

    public UIElement TitleBarElement => TitleBar;

    public void SetCaptionInset(double width) => CaptionColumn.Width = new GridLength(Math.Max(0, width));

    public async Task StartAsync()
    {
        AdminBadge.Visibility = Elevation.IsElevated ? Visibility.Visible : Visibility.Collapsed;
        BuildProfileMenu();
        UpdateToolTips();
        ApplyMotion();

        var notes = new List<string>(_services.StartupNotes);
        var document = await LoadInitialWorkspaceAsync(notes);
        _workspace.Load(document);
        _workspace.Save(); // Overwrite the previous session's layout right away.
        RememberWorkspace(document.Id);
        UpdateWorkspaceLabel();
        UpdateEmptyState();
        Log.Info($"Workspace '{document.Name}' opened with {document.Tabs.Count} tab(s).");

        if (TerminalHost.TerminalWebEnvironment.GetRuntimeVersion() is null)
        {
            notes.Insert(0, "Microsoft Edge WebView2 Runtime was not found. Install it from https://developer.microsoft.com/microsoft-edge/webview2/ — terminals cannot render without it.");
        }

        if (notes.Count > 0)
        {
            ShowNotice(string.Join(Environment.NewLine, notes.Distinct()), InfoBarSeverity.Warning);
        }
    }

    // ── Command registry ─────────────────────────────────────────────────────

    private void RegisterCommands()
    {
        var c = _services.Commands;
        void Bind(string id, Action run, Func<bool>? canRun = null) => c.Register(DefaultCommands.Get(id), _ => run(), canRun);

        bool HasPane() => _workspace.FocusedPane is not null;
        bool HasTab() => _workspace.SelectedTab is not null;

        Bind(CommandIds.NewTab, () => _workspace.NewTab());
        Bind(CommandIds.NewTerminalWithProfile, OpenProfilePalette);
        Bind(CommandIds.CloseTab, () => _ = CloseTabAsync(_workspace.SelectedTab!), HasTab);
        Bind(CommandIds.NextTab, () => _workspace.SelectRelative(1), HasTab);
        Bind(CommandIds.PreviousTab, () => _workspace.SelectRelative(-1), HasTab);
        Bind(CommandIds.RenameTab, () => _ = RenameTabAsync(), HasTab);

        Bind(CommandIds.SplitRight, () => Split(PaneArrangement.SideBySide), HasTab);
        Bind(CommandIds.SplitDown, () => Split(PaneArrangement.Stacked), HasTab);
        Bind(CommandIds.ClosePane, () => _ = ClosePaneAsync(), HasPane);
        Bind(CommandIds.FocusNextPane, () => _workspace.SelectedTab?.CycleFocus(1), HasTab);
        Bind(CommandIds.FocusPreviousPane, () => _workspace.SelectedTab?.CycleFocus(-1), HasTab);
        Bind(CommandIds.FocusLeft, () => _workspace.SelectedTab?.MoveFocus(FocusDirection.Left), HasTab);
        Bind(CommandIds.FocusRight, () => _workspace.SelectedTab?.MoveFocus(FocusDirection.Right), HasTab);
        Bind(CommandIds.FocusUp, () => _workspace.SelectedTab?.MoveFocus(FocusDirection.Up), HasTab);
        Bind(CommandIds.FocusDown, () => _workspace.SelectedTab?.MoveFocus(FocusDirection.Down), HasTab);
        Bind(CommandIds.ResizeLeft, () => _workspace.SelectedTab?.ResizeFocused(FocusDirection.Left), HasTab);
        Bind(CommandIds.ResizeRight, () => _workspace.SelectedTab?.ResizeFocused(FocusDirection.Right), HasTab);
        Bind(CommandIds.ResizeUp, () => _workspace.SelectedTab?.ResizeFocused(FocusDirection.Up), HasTab);
        Bind(CommandIds.ResizeDown, () => _workspace.SelectedTab?.ResizeFocused(FocusDirection.Down), HasTab);
        Bind(CommandIds.EqualizePanes, () => _workspace.SelectedTab?.Equalize(), HasTab);
        Bind(CommandIds.RestartPane, () => _ = _workspace.FocusedPane!.RestartAsync(), HasPane);

        Bind(CommandIds.Copy, () => _workspace.FocusedPane!.View.RunAction("copy"), HasPane);
        Bind(CommandIds.Paste, () => _ = PasteAsync(), HasPane);
        Bind(CommandIds.Search, OpenFind, HasPane);
        Bind(CommandIds.SelectAll, () => _workspace.FocusedPane!.View.RunAction("selectAll"), HasPane);
        Bind(CommandIds.ClearTerminal, () => _workspace.FocusedPane!.View.RunAction("clear"), HasPane);
        Bind(CommandIds.FontIncrease, () => _services.UpdateSettings(s => s.Terminal.FontSize = Math.Min(TerminalSettings.MaxFontSize, s.Terminal.FontSize + 1)));
        Bind(CommandIds.FontDecrease, () => _services.UpdateSettings(s => s.Terminal.FontSize = Math.Max(TerminalSettings.MinFontSize, s.Terminal.FontSize - 1)));
        Bind(CommandIds.FontReset, () => _services.UpdateSettings(s => s.Terminal.FontSize = new TerminalSettings().FontSize));

        Bind(CommandIds.CommandPalette, OpenCommandPalette);
        Bind(CommandIds.OpenSettings, () => SettingsPanel.Open(_services.Theme.AnimationsEnabled));
        Bind(CommandIds.ToggleTheme, ToggleTheme);
        Bind(CommandIds.ShowShortcuts, () => SettingsPanel.Open(_services.Theme.AnimationsEnabled, SettingsView.ShortcutsSection));
        Bind(CommandIds.NewAdminWindow, OpenAdministratorWindow, () => !Elevation.IsElevated);

        Bind(CommandIds.SwitchWorkspace, OpenWorkspacePalette);
        Bind(CommandIds.NewWorkspace, () => _ = NewWorkspaceAsync());
        Bind(CommandIds.RenameWorkspace, () => _ = RenameWorkspaceAsync());
        Bind(CommandIds.SaveWorkspace, SaveWorkspace);
        Bind(CommandIds.DeleteWorkspace, OpenDeleteWorkspacePalette);

        var conflicts = c.ApplyOverrides(_services.Settings.KeyBindings);
        if (conflicts.Count > 0)
        {
            Log.Warn(string.Join(" ", conflicts));
        }
    }

    private void Execute(string id)
    {
        if (!_services.Commands.TryExecute(id))
        {
            Log.Info($"Command '{id}' is not available right now.");
        }
    }

    private void ExecuteFromRenderer(string id)
    {
        // The renderer can only name commands; it cannot define them. Unknown ids are ignored.
        if (_services.Commands.Contains(id))
        {
            Execute(id);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (Palette.IsOpen || Dialogs.IsOpen)
        {
            return;
        }

        if (KeyMapper.FromVirtualKey(e.Key) is not { } chord || _services.Commands.FindCommand(chord) is not { } id)
        {
            return;
        }

        // Inside text fields, leave plain Ctrl+<key> editing shortcuts (copy, paste, select all) to the field.
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox or PasswordBox or NumberBox &&
            chord.Ctrl && !chord.Shift && !chord.Alt && id is not CommandIds.OpenSettings)
        {
            return;
        }

        if (_services.Commands.TryExecute(id))
        {
            e.Handled = true;
        }
    }

    // ── Launch requests ──────────────────────────────────────────────────────

    /// <summary>Opens the terminal a forwarded launch asked for (e.g. "Open ShellFlow here (new pane)").</summary>
    public void OpenFromLaunch(Core.StartupArguments request)
    {
        if (Palette.IsOpen)
        {
            Palette.Close();
        }

        if (SettingsPanel.IsOpen)
        {
            SettingsPanel.Close();
        }

        if (request.Script is { } script)
        {
            if (ResolveScript(script) is { } file)
            {
                _workspace.NewTab(ScriptProfile, Path.GetDirectoryName(file), file);
            }
            else
            {
                ShowNotice($"The script \"{script}\" was not found.", InfoBarSeverity.Warning);
            }

            return;
        }

        var directory = request.Directory;
        if (directory is not null && !Directory.Exists(directory))
        {
            ShowNotice($"The folder \"{directory}\" does not exist.", InfoBarSeverity.Warning);
            directory = null;
        }

        ShellProfile? profile = null;
        if (request.ProfileId is { } id)
        {
            profile = _services.Profiles.Find(id);
            if (profile is null)
            {
                ShowNotice($"No shell profile named \"{id}\" exists; the default shell was used.", InfoBarSeverity.Warning);
            }
        }

        var tab = _workspace.SelectedTab;
        if (request.Mode == Core.LaunchMode.NewPane && tab is not null)
        {
            if (tab.FittingArrangement(tab.AutoArrangement()) is { } arrangement)
            {
                tab.Split(arrangement, profile ?? _services.Profiles.Default, directory);
                return;
            }

            ShowNotice("The current tab has no room for another pane, so the folder opened in a new tab.", InfoBarSeverity.Informational, autoClose: true);
        }

        _workspace.NewTab(profile, directory);
    }

    /// <summary>
    /// Opens an elevated ShellFlow window (after the UAC prompt) with the focused pane's shell and folder. The current
    /// window and its shells are not affected.
    /// </summary>
    private void OpenAdministratorWindow()
    {
        var pane = _workspace.FocusedPane;
        var profileId = pane is { Leaf.Script: null } ? pane.Profile.Id : null;
        try
        {
            Elevation.LaunchElevated(pane?.CurrentFolder, profileId);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error("Could not start an administrator window", ex);
            ShowNotice($"Could not open an administrator window: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    /// <summary>Scripts always run in Command Prompt, like a double-click in Explorer.</summary>
    private ShellProfile ScriptProfile =>
        _services.Profiles.Find(ProfileDetector.CmdId) ?? new ShellProfile
        {
            Id = ProfileDetector.CmdId,
            Name = "Command Prompt",
            Executable = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Icon = ProfileIcons.Cmd,
        };

    private static string? ResolveScript(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return Core.StartupArguments.IsScript(full) && File.Exists(full) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    // ── Tabs and panes ───────────────────────────────────────────────────────

    private void Split(PaneArrangement arrangement)
    {
        var tab = _workspace.SelectedTab;
        if (tab is null)
        {
            return;
        }

        if (tab.PaneCount >= WorkspaceValidator.MaxPanesPerTab)
        {
            ShowNotice($"A tab can hold up to {WorkspaceValidator.MaxPanesPerTab} panes. Open a new tab for more terminals.", InfoBarSeverity.Informational, autoClose: true);
            return;
        }

        if (!tab.CanSplit(arrangement))
        {
            // Splitting further would make panes too small to use (and some would effectively disappear).
            var other = arrangement == PaneArrangement.SideBySide ? PaneArrangement.Stacked : PaneArrangement.SideBySide;
            var hint = tab.CanSplit(other)
                ? $"Try {(other == PaneArrangement.Stacked ? "splitting down" : "splitting right")}, make the window larger, or open a new tab."
                : "Make the window larger, close a pane, or open a new tab.";
            ShowNotice($"This pane is too small to split {(arrangement == PaneArrangement.SideBySide ? "right" : "down")}. {hint}", InfoBarSeverity.Informational, autoClose: true);
            return;
        }

        tab.Split(arrangement);
    }

    private async Task ClosePaneAsync()
    {
        var tab = _workspace.SelectedTab;
        var pane = _workspace.FocusedPane;
        if (tab is null || pane is null)
        {
            return;
        }

        if (pane.HasRunningCommand && !await ConfirmStopRunningAsync("Close pane?", "A command is still running in this pane. Closing it will stop the process.", "Close pane"))
        {
            return;
        }

        await tab.ClosePaneAsync(pane.Id);
    }

    private async Task CloseTabAsync(TabController tab)
    {
        if (tab is null)
        {
            return;
        }

        var running = tab.RunningCommandCount;
        if (running > 0 && !await ConfirmStopRunningAsync(
                "Close tab?",
                running == 1 ? "A command is still running in this tab. Closing it will stop the process." : $"{running} panes in this tab are running commands. Closing the tab will stop them.",
                "Close tab"))
        {
            return;
        }

        await _workspace.CloseTabAsync(tab);
    }

    private async Task<bool> ConfirmStopRunningAsync(string title, string message, string primary)
    {
        if (!_services.Settings.Behavior.ConfirmCloseRunning)
        {
            return true;
        }

        var result = await Dialogs.ConfirmAsync(XamlRoot, title, message, primary, "Cancel", "Don't ask me again", destructive: true);
        if (result.Confirmed && result.DontAskAgain)
        {
            _services.UpdateSettings(s => s.Behavior.ConfirmCloseRunning = false);
        }

        return result.Confirmed;
    }

    private async Task RenameTabAsync()
    {
        var tab = _workspace.SelectedTab;
        if (tab is null)
        {
            return;
        }

        var name = await Dialogs.PromptAsync(XamlRoot, "Rename tab", tab.CustomTitle ?? tab.Title, "Rename", "Leave empty to use the shell's title");
        if (name is not null)
        {
            tab.CustomTitle = name;
        }

        tab.Focus();
    }

    private void OnTabStripSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection)
        {
            return;
        }

        // Reordering removes and re-inserts the item, briefly clearing the selection; ignore that transient state.
        if (TabStrip.SelectedItem is TabController tab)
        {
            _workspace.SelectedTab = tab;
        }
    }

    private void SyncTabStripSelection()
    {
        _syncingSelection = true;
        try
        {
            TabStrip.SelectedItem = _workspace.SelectedTab;
            if (_workspace.SelectedTab is { } selected)
            {
                // Scrolling while the collection is still changing crashes ListView; wait for layout.
                DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                {
                    if (TabStrip.IsLoaded && _workspace.Tabs.Contains(selected))
                    {
                        TabStrip.ScrollIntoView(selected);
                    }
                });
            }
        }
        finally
        {
            _syncingSelection = false;
        }

        UpdateStatus();
    }

    private const double TabMinWidth = 96;
    private const double TabMaxWidth = 220;
    private const double TabSpacing = 4; // SfTabItemStyle right margin.

    /// <summary>
    /// Keeps the tab strip inside its title-bar column: tabs shrink (Chrome-style) down to a minimum width, then the strip
    /// scrolls. Without the cap the strip would grow under the toolbar and the window's caption buttons.
    /// </summary>
    private void UpdateTabSizing()
    {
        foreach (var tab in _workspace.Tabs)
        {
            tab.InSidePanel = TabsInSidePanel;
        }

        if (TabsInSidePanel)
        {
            // Stacked tabs simply fill the side panel's width; the list scrolls vertically.
            TabStrip.MaxWidth = double.PositiveInfinity;
            foreach (var tab in _workspace.Tabs)
            {
                tab.TabWidth = double.NaN;
            }
        }
        else
        {
            var column = TitleBar.ColumnDefinitions[2].ActualWidth;
            if (column <= 0)
            {
                return;
            }

            var available = Math.Max(0, column - NewTabButtons.ActualWidth - 4);
            TabStrip.MaxWidth = available;

            var count = Math.Max(1, _workspace.Tabs.Count);
            var width = Math.Clamp(Math.Floor((available / count) - TabSpacing), TabMinWidth, TabMaxWidth);
            foreach (var tab in _workspace.Tabs)
            {
                tab.TabWidth = width;
            }
        }

        if (_workspace.SelectedTab is { } selected)
        {
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (TabStrip.IsLoaded && _workspace.Tabs.Contains(selected))
                {
                    TabStrip.ScrollIntoView(selected);
                }
            });
        }

        InteractiveRegionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private TabPosition? _tabPosition;

    /// <summary>
    /// Puts the tab area where Appearance.TabPosition says: stacked in the side panel next to the workspace, or as a
    /// horizontal strip in the title bar's free column.
    /// </summary>
    private void ApplyTabPosition()
    {
        var position = _services.Settings.Appearance.TabPosition;
        if (_tabPosition == position)
        {
            return;
        }

        _tabPosition = position;
        var side = position == TabPosition.Left;

        // FrameworkElement.Parent is not reliable before the first layout pass, so detach from both possible hosts.
        BodyGrid.Children.Remove(TabArea);
        TitleBar.Children.Remove(TabArea);
        TabArea.RowDefinitions.Clear();
        TabArea.ColumnDefinitions.Clear();

        // Detach the items while the panel and container style change so every tab container is rebuilt with them.
        _syncingSelection = true;
        TabStrip.ItemsSource = null;
        _syncingSelection = false;

        if (side)
        {
            TabArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            TabArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            TabArea.Width = 210;
            TabArea.Margin = new Thickness(8, 2, 2, 6);
            TabArea.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetColumn(TabArea, 0);

            Grid.SetRow(NewTabButtons, 0);
            Grid.SetColumn(NewTabButtons, 0);
            NewTabButtons.Margin = new Thickness(0, 0, 0, 4);
            NewTabButtons.VerticalAlignment = VerticalAlignment.Stretch;

            Grid.SetRow(TabStrip, 1);
            Grid.SetColumn(TabStrip, 0);
            TabStrip.Height = double.NaN;
            TabStrip.VerticalAlignment = VerticalAlignment.Top;
            TabStrip.ItemsPanel = (ItemsPanelTemplate)Resources["SideTabsPanel"];
            TabStrip.ItemContainerStyle = (Style)Application.Current.Resources["SfSideTabItemStyle"];
            ScrollViewer.SetHorizontalScrollMode(TabStrip, ScrollMode.Disabled);
            ScrollViewer.SetHorizontalScrollBarVisibility(TabStrip, ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollMode(TabStrip, ScrollMode.Enabled);
            ScrollViewer.SetVerticalScrollBarVisibility(TabStrip, ScrollBarVisibility.Auto);

            BodyGrid.Children.Insert(0, TabArea);
            WorkspaceArea.Margin = new Thickness(4, 0, 6, 0);
        }
        else
        {
            TabArea.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TabArea.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TabArea.Width = double.NaN;
            TabArea.Margin = new Thickness(0);
            TabArea.HorizontalAlignment = HorizontalAlignment.Left;
            Grid.SetColumn(TabArea, 2);

            Grid.SetRow(NewTabButtons, 0);
            Grid.SetColumn(NewTabButtons, 1);
            NewTabButtons.Margin = new Thickness(2, 0, 0, 0);
            NewTabButtons.VerticalAlignment = VerticalAlignment.Center;

            Grid.SetRow(TabStrip, 0);
            Grid.SetColumn(TabStrip, 0);
            TabStrip.Height = 34;
            TabStrip.VerticalAlignment = VerticalAlignment.Center;
            TabStrip.ItemsPanel = (ItemsPanelTemplate)Resources["TopTabsPanel"];
            TabStrip.ItemContainerStyle = (Style)Application.Current.Resources["SfTabItemStyle"];
            ScrollViewer.SetHorizontalScrollMode(TabStrip, ScrollMode.Enabled);
            ScrollViewer.SetHorizontalScrollBarVisibility(TabStrip, ScrollBarVisibility.Hidden);
            ScrollViewer.SetVerticalScrollMode(TabStrip, ScrollMode.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(TabStrip, ScrollBarVisibility.Disabled);

            TitleBar.Children.Add(TabArea);
            WorkspaceArea.Margin = new Thickness(6, 0, 6, 0);
        }

        TabStrip.ItemsSource = _workspace.Tabs;
        UpdateTabSizing();
        SyncTabStripSelection();
        InteractiveRegionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnTabStripWheel(object sender, PointerRoutedEventArgs e)
    {
        if (FindScrollViewer(TabStrip) is not { } scroller || scroller.ScrollableWidth <= 0)
        {
            return;
        }

        var delta = e.GetCurrentPoint(TabStrip).Properties.MouseWheelDelta;
        scroller.ChangeView(scroller.HorizontalOffset - delta, null, null);
        e.Handled = true;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer)
            {
                return viewer;
            }

            if (FindScrollViewer(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private void OnTabCloseClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TabController tab })
        {
            _ = CloseTabAsync(tab);
        }
    }

    private void OnNewTabClick(object sender, RoutedEventArgs e) => Execute(CommandIds.NewTab);

    private void OnEmptyPaletteClick(object sender, RoutedEventArgs e) => Execute(CommandIds.CommandPalette);

    private void OnToolbarClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id })
        {
            Execute(id);
        }
    }

    private void BuildProfileMenu()
    {
        ProfileMenu.Items.Clear();
        foreach (var profile in _services.Profiles.Visible)
        {
            var item = new MenuFlyoutItem
            {
                Text = profile.Name,
                Icon = new FontIcon { Glyph = ProfileGlyphs.For(profile), FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["SfIconFont"] },
            };
            if (profile.Id == _services.Profiles.Default.Id)
            {
                item.KeyboardAcceleratorTextOverride = "Default";
            }

            item.Click += (_, _) => _workspace.NewTab(profile);
            ProfileMenu.Items.Add(item);
        }

        ProfileMenu.Items.Add(new MenuFlyoutSeparator());
        if (!Elevation.IsElevated)
        {
            var admin = new MenuFlyoutItem
            {
                Text = "New administrator window",
                Icon = new FontIcon { Glyph = "" },
                KeyboardAcceleratorTextOverride = _services.Commands.GetShortcut(CommandIds.NewAdminWindow)?.ToString() ?? string.Empty,
            };
            admin.Click += (_, _) => Execute(CommandIds.NewAdminWindow);
            ProfileMenu.Items.Add(admin);
        }

        var settings = new MenuFlyoutItem { Text = "Manage shells…", Icon = new FontIcon { Glyph = "" } };
        settings.Click += (_, _) => SettingsPanel.Open(_services.Theme.AnimationsEnabled, SettingsView.ProfilesSection);
        ProfileMenu.Items.Add(settings);
    }

    // ── Clipboard ────────────────────────────────────────────────────────────

    private void CopyToClipboard(string text)
    {
        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text);
            Clipboard.SetContent(package);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            Log.Warn($"Clipboard write failed: {ex.Message}");
            ShowNotice("Could not copy to the clipboard. Another application may be using it.", InfoBarSeverity.Warning);
        }
    }

    private async Task PasteAsync()
    {
        var pane = _workspace.FocusedPane;
        if (pane is null)
        {
            return;
        }

        string text;
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
            {
                return;
            }

            text = await content.GetTextAsync();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            Log.Warn($"Clipboard read failed: {ex.Message}");
            return;
        }

        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (text.Length > 4 * 1024 * 1024)
        {
            ShowNotice("The clipboard text is larger than 4 MB and was not pasted.", InfoBarSeverity.Warning);
            return;
        }

        var lines = text.TrimEnd('\r', '\n').Split('\n').Length;
        if (lines > 1 && _services.Settings.Behavior.ConfirmMultilinePaste)
        {
            var result = await Dialogs.ConfirmAsync(
                XamlRoot,
                "Paste multiple lines?",
                $"The clipboard contains {lines} lines. Many shells run each line as soon as it is pasted.",
                "Paste",
                "Cancel",
                "Don't warn me about multi-line pastes");
            if (result.DontAskAgain && result.Confirmed)
            {
                _services.UpdateSettings(s => s.Behavior.ConfirmMultilinePaste = false);
            }

            if (!result.Confirmed)
            {
                pane.Focus();
                return;
            }
        }

        pane.View.Paste(text);
        pane.Focus();
    }

    // ── Find ─────────────────────────────────────────────────────────────────

    private void OpenFind() => FindBar.Open(_services.Theme.AnimationsEnabled);

    private void OnSearchRequested(object? sender, SearchRequest request) =>
        _workspace.FocusedPane?.View.Find(request.Query, request.Backwards, request.CaseSensitive, request.Regex, request.WholeWord, request.Incremental);

    // ── Palette modes ────────────────────────────────────────────────────────

    private void OpenCommandPalette()
    {
        if (Palette.IsOpen)
        {
            Palette.Close();
            return;
        }

        var items = _services.Commands.Definitions
            .Where(d => d.ShowInPalette && d.Id != CommandIds.CommandPalette && _services.Commands.CanExecute(d.Id))
            .Select(d => new PaletteItem
            {
                Title = d.Title,
                Subtitle = d.Category,
                Glyph = d.Glyph ?? "",
                Shortcut = _services.Commands.GetShortcut(d.Id)?.ToString(),
                Keywords = $"{d.Category} {d.Keywords}",
                Invoke = () => Execute(d.Id),
            })
            .ToList();

        ShowPalette(items, null, "Type a command…");
    }

    private void OpenProfilePalette()
    {
        var items = _services.Profiles.Visible.Select(p => new PaletteItem
        {
            Title = p.Name,
            Subtitle = p.Id == _services.Profiles.Default.Id ? $"Default · {p.Executable}" : p.Executable,
            Glyph = ProfileGlyphs.For(p),
            Invoke = () => _workspace.NewTab(p),
        }).ToList();

        ShowPalette(items, "New terminal", "Choose a shell…");
    }

    private void OpenWorkspacePalette()
    {
        var items = new List<PaletteItem>();
        foreach (var summary in _services.Workspaces.List())
        {
            var current = summary.Id == _workspace.Id;
            items.Add(new PaletteItem
            {
                Title = summary.Name,
                Subtitle = (current ? "Current · " : string.Empty) + $"{summary.TabCount} tab{(summary.TabCount == 1 ? string.Empty : "s")}" + (summary.LastSaved is { } saved ? $" · saved {saved.LocalDateTime:g}" : string.Empty),
                Glyph = current ? "" : "",
                Invoke = () => _ = SwitchWorkspaceAsync(summary.Id),
            });
        }

        items.Add(new PaletteItem { Title = "New workspace…", Glyph = "", Invoke = () => _ = NewWorkspaceAsync() });
        ShowPalette(items, "Workspaces", "Switch to a workspace…");
    }

    private void OpenDeleteWorkspacePalette()
    {
        var items = _services.Workspaces.List().Select(summary => new PaletteItem
        {
            Title = summary.Name,
            Subtitle = summary.Id == _workspace.Id ? "Current workspace" : $"{summary.TabCount} tabs",
            Glyph = "",
            Invoke = () => _ = DeleteWorkspaceAsync(summary),
        }).ToList();

        ShowPalette(items, "Delete", "Choose a workspace to delete…");
    }

    private void ShowPalette(IReadOnlyList<PaletteItem> items, string? mode, string placeholder)
    {
        Palette.Open(items, mode, placeholder, _services.Theme.AnimationsEnabled, _services.Theme.FluidMotion);
        InteractiveRegionsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Workspaces ───────────────────────────────────────────────────────────

    /// <summary>
    /// Every launch starts clean: one tab with a single terminal. The last workspace's name is kept so it does not
    /// multiply, but its previous tabs and splits are discarded.
    /// </summary>
    private async Task<WorkspaceDocument> LoadInitialWorkspaceAsync(List<string> notes)
    {
        var document = NewWorkspaceDocument("Default");

        // "Open ShellFlow here" / command-line options decide the first terminal's folder and shell.
        var first = (PaneLeaf)document.Tabs[0].Root!;
        var startup = _services.Startup;
        if (startup.Directory is { } directory)
        {
            if (Directory.Exists(directory))
            {
                first.StartingDirectory = directory;
            }
            else
            {
                notes.Add($"The folder \"{directory}\" does not exist; ShellFlow started in the default folder.");
            }
        }

        if (startup.Script is { } script)
        {
            if (ResolveScript(script) is { } file)
            {
                first.Script = file;
                first.ProfileId = ScriptProfile.Id;
                first.StartingDirectory = Path.GetDirectoryName(file);
            }
            else
            {
                notes.Add($"The script \"{script}\" was not found.");
            }
        }

        if (startup.ProfileId is { } profileId)
        {
            if (_services.Profiles.Find(profileId) is { } profile)
            {
                first.ProfileId = profile.Id;
            }
            else
            {
                notes.Add($"No shell profile named \"{profileId}\" exists; the default shell was used.");
            }
        }

        // The administrator window keeps its own workspace so it never overwrites the normal window's.
        if (Elevation.IsElevated)
        {
            document.Id = "administrator";
            document.Name = "Administrator";
            await NormalizeAsync(document, notes);
            return document;
        }

        var lastId = _services.Workspaces.LoadState().LastWorkspaceId;
        if (lastId is not null && SafeLoad(lastId) is { Status: not LoadStatus.Recovered } last)
        {
            document.Id = last.Value.Id;
            document.Name = WorkspaceValidator.NormalizeName(last.Value.Name);
        }

        await NormalizeAsync(document, notes);
        return document;
    }

    private LoadResult<WorkspaceDocument>? SafeLoad(string id)
    {
        try
        {
            return _services.Workspaces.Load(id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Error($"Could not load workspace {id}", ex);
            return null;
        }
    }

    private async Task NormalizeAsync(WorkspaceDocument document, List<string> notes)
    {
        // WSL profiles are discovered in the background; wait briefly if this workspace uses them.
        var usesWsl = document.Tabs.Any(t => PaneTree.Leaves(t.Root).Any(l => l.ProfileId.StartsWith(ProfileDetector.WslIdPrefix, StringComparison.OrdinalIgnoreCase)));
        if (usesWsl)
        {
            await Task.WhenAny(_services.Profiles.WslDetection, Task.Delay(TimeSpan.FromSeconds(5)));
        }

        notes.AddRange(WorkspaceValidator.Normalize(document, id => _services.Profiles.Find(id) is not null, _services.Profiles.Default.Id));
    }

    private WorkspaceDocument NewWorkspaceDocument(string name)
    {
        var leaf = new PaneLeaf { ProfileId = _services.Profiles.Default.Id };
        return new WorkspaceDocument
        {
            Name = name,
            Tabs = [new TabDocument { Root = leaf, FocusedPaneId = leaf.Id }],
        };
    }

    private async Task<bool> ConfirmLeaveWorkspaceAsync()
    {
        var running = _workspace.RunningCommandCount;
        return running == 0 || await ConfirmStopRunningAsync(
            "Leave this workspace?",
            $"{running} {(running == 1 ? "pane is" : "panes are")} running commands. Switching workspaces stops every shell in this one (its layout is saved).",
            "Switch");
    }

    private async Task OpenWorkspaceAsync(WorkspaceDocument document)
    {
        _workspace.Save();
        await _workspace.UnloadAsync();
        var notes = new List<string>();
        await NormalizeAsync(document, notes);
        _workspace.Load(document);
        _workspace.Save();
        RememberWorkspace(document.Id);
        UpdateWorkspaceLabel();
        if (notes.Count > 0)
        {
            ShowNotice(string.Join(Environment.NewLine, notes), InfoBarSeverity.Warning);
        }
    }

    private async Task SwitchWorkspaceAsync(string id)
    {
        if (id == _workspace.Id || !await ConfirmLeaveWorkspaceAsync())
        {
            return;
        }

        var loaded = SafeLoad(id);
        if (loaded is null || loaded.Status == LoadStatus.Recovered)
        {
            ShowNotice("That workspace could not be read.", InfoBarSeverity.Error);
            return;
        }

        await OpenWorkspaceAsync(loaded.Value);
    }

    private async Task NewWorkspaceAsync()
    {
        var name = await Dialogs.PromptAsync(XamlRoot, "New workspace", string.Empty, "Create", "Workspace name");
        if (name is null || !await ConfirmLeaveWorkspaceAsync())
        {
            return;
        }

        await OpenWorkspaceAsync(NewWorkspaceDocument(WorkspaceValidator.NormalizeName(name)));
    }

    private async Task RenameWorkspaceAsync()
    {
        var name = await Dialogs.PromptAsync(XamlRoot, "Rename workspace", _workspace.Name, "Rename");
        if (name is not null)
        {
            _workspace.Rename(name);
            UpdateWorkspaceLabel();
        }
    }

    private void SaveWorkspace()
    {
        _workspace.Save();
        ShowNotice($"Workspace \"{_workspace.Name}\" saved.", InfoBarSeverity.Success, autoClose: true);
    }

    private async Task DeleteWorkspaceAsync(WorkspaceSummary summary)
    {
        var current = summary.Id == _workspace.Id;
        var result = await Dialogs.ConfirmAsync(
            XamlRoot,
            $"Delete \"{summary.Name}\"?",
            current
                ? "This removes the saved layout and closes every shell in this workspace. This cannot be undone."
                : "This removes the saved layout. This cannot be undone.",
            "Delete",
            destructive: true);
        if (!result.Confirmed)
        {
            return;
        }

        if (current)
        {
            await _workspace.UnloadAsync();
        }

        try
        {
            _services.Workspaces.Delete(summary.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Could not delete workspace", ex);
            ShowNotice($"Could not delete the workspace: {ex.Message}", InfoBarSeverity.Error);
        }

        if (current)
        {
            var next = _services.Workspaces.List().FirstOrDefault();
            var document = next is not null && SafeLoad(next.Id) is { Status: not LoadStatus.Recovered } loaded ? loaded.Value : NewWorkspaceDocument("Default");
            var notes = new List<string>();
            await NormalizeAsync(document, notes);
            _workspace.Load(document);
            _workspace.Save();
            RememberWorkspace(document.Id);
            UpdateWorkspaceLabel();
        }
    }

    private void RememberWorkspace(string id)
    {
        if (Elevation.IsElevated)
        {
            return; // The normal window decides which workspace is "last used".
        }

        try
        {
            var state = _services.Workspaces.LoadState();
            state.LastWorkspaceId = id;
            _services.Workspaces.SaveState(state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not save state: {ex.Message}");
        }
    }

    private void OnWorkspaceButtonClick(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
        foreach (var summary in _services.Workspaces.List())
        {
            var item = new ToggleMenuFlyoutItem { Text = summary.Name, IsChecked = summary.Id == _workspace.Id };
            item.Click += (_, _) => _ = SwitchWorkspaceAsync(summary.Id);
            menu.Items.Add(item);
        }

        if (menu.Items.Count == 0)
        {
            menu.Items.Add(new ToggleMenuFlyoutItem { Text = _workspace.Name, IsChecked = true });
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        AddMenuCommand(menu, CommandIds.NewWorkspace);
        AddMenuCommand(menu, CommandIds.RenameWorkspace);
        AddMenuCommand(menu, CommandIds.SaveWorkspace);
        AddMenuCommand(menu, CommandIds.DeleteWorkspace);
        menu.ShowAt(WorkspaceButton);
    }

    private void AddMenuCommand(MenuFlyout menu, string id)
    {
        var definition = _services.Commands.Get(id)!;
        var item = new MenuFlyoutItem
        {
            Text = definition.Title,
            Icon = new FontIcon { Glyph = definition.Glyph ?? "" },
            KeyboardAcceleratorTextOverride = _services.Commands.GetShortcut(id)?.ToString() ?? string.Empty,
        };
        item.Click += (_, _) => Execute(id);
        menu.Items.Add(item);
    }

    // ── Settings, theme, motion ──────────────────────────────────────────────

    private void OnSettingsChanged()
    {
        _services.Theme.Apply(_services.Settings.Appearance);
        ApplyTabPosition();
        ApplyRendererOptions();
        BuildProfileMenu();
        UpdateToolTips();
    }

    private void ApplyRendererOptions() => _workspace.ApplyRendererOptions(_services.BuildRendererOptions());

    private void ApplyMotion()
    {
        var enabled = _services.Theme.AnimationsEnabled;
        TabStrip.ItemContainerTransitions = enabled
            ? [new AddDeleteThemeTransition(), new ReorderThemeTransition()]
            : [];
        var motion = _services.Settings.Appearance.Animations switch
        {
            AnimationPreference.Off => "motion:off",
            AnimationPreference.Full => "motion:full",
            _ => enabled ? "motion:system" : "motion:off",
        };
        foreach (var pane in _workspace.Tabs.SelectMany(t => t.Panes))
        {
            pane.View.RunAction(motion);
        }
    }

    private void ToggleTheme()
    {
        var next = _services.Theme.ActualTheme == ElementTheme.Light ? ThemePreference.Dark : ThemePreference.Light;
        _services.UpdateSettings(s => s.Appearance.Theme = next);
    }

    private void UpdateToolTips()
    {
        void Tip(FrameworkElement element, string commandId)
        {
            var definition = _services.Commands.Get(commandId);
            var shortcut = _services.Commands.GetShortcut(commandId);
            ToolTipService.SetToolTip(element, shortcut is null ? definition?.Title : $"{definition?.Title} ({shortcut})");
        }

        Tip(NewTabButton, CommandIds.NewTab);
        Tip(SplitRightButton, CommandIds.SplitRight);
        Tip(SplitDownButton, CommandIds.SplitDown);
        Tip(SearchButton, CommandIds.Search);
        Tip(PaletteButton, CommandIds.CommandPalette);
        Tip(SettingsButton, CommandIds.OpenSettings);
        Tip(WorkspaceButton, CommandIds.SwitchWorkspace);
        EmptyHintText.Text = $"Open a new tab with {_services.Commands.GetShortcut(CommandIds.NewTab)?.ToString() ?? "the + button"}, or pick a shell from the command palette ({_services.Commands.GetShortcut(CommandIds.CommandPalette)?.ToString() ?? "toolbar"}).";
    }

    // ── Status and notices ───────────────────────────────────────────────────

    private void UpdateWorkspaceLabel()
    {
        WorkspaceNameText.Text = _workspace.Name;
        WorkspaceStatusText.Text = _workspace.Name;
        AutomationPropertiesHelper.SetName(WorkspaceButton, $"Workspace: {_workspace.Name}");
    }

    private void UpdateEmptyState()
    {
        var empty = _workspace.Tabs.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        StateGroup.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var pane = _workspace.FocusedPane;
        UpdateWorkspaceLabel();
        if (pane is null)
        {
            ShellText.Text = string.Empty;
            DirectoryText.Text = string.Empty;
            SizeText.Text = string.Empty;
            DirectoryGroup.Visibility = Visibility.Collapsed;
            return;
        }

        var theme = ActualTheme;
        (StateText.Text, StateDot.Fill) = pane.State switch
        {
            PaneState.Starting => ("Starting…", ThemeResources.Brush("SfAccentBrush", theme)),
            PaneState.Running when pane.HasRunningCommand => ("Running command", ThemeResources.Brush("SfAccentBrush", theme)),
            PaneState.Running => ("Ready", ThemeResources.Brush("SfSuccessBrush", theme)),
            PaneState.Exited => ($"Exited ({pane.ExitCode})", ThemeResources.Brush("SfWarningBrush", theme)),
            _ => ("Failed to start", ThemeResources.Brush("SfErrorBrush", theme)),
        };

        ShellGlyph.Glyph = ProfileGlyphs.For(pane.Profile);
        ShellText.Text = pane.Profile.Name;

        // Only show a directory the shell actually reported; otherwise say where it started.
        var directory = pane.WorkingDirectory ?? (pane.StartedIn is not null ? $"started in {pane.StartedIn}" : null);
        DirectoryText.Text = directory ?? string.Empty;
        DirectoryGroup.Visibility = directory is null ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(DirectoryGroup, pane.WorkingDirectory is null ? "The shell has not reported its current directory." : "Current directory reported by the shell");

        SizeText.Text = pane.View.Columns > 0 ? $"{pane.View.Columns} × {pane.View.Rows}" : string.Empty;
    }

    public void ShowNotice(string message, InfoBarSeverity severity, bool autoClose = false)
    {
        NoticeBar.Message = message;
        NoticeBar.Severity = severity;
        NoticeBar.IsOpen = true;
        if (autoClose)
        {
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(3);
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                if (NoticeBar.Message == message)
                {
                    NoticeBar.IsOpen = false;
                }
            };
            timer.Start();
        }
    }

    // ── Shutdown ─────────────────────────────────────────────────────────────

    /// <summary>Asks before closing the window if commands are running. Returns true when it is OK to close.</summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        var running = _workspace.RunningCommandCount;
        return running == 0 || await ConfirmStopRunningAsync(
            "Close ShellFlow?",
            $"{running} {(running == 1 ? "pane is" : "panes are")} still running commands. Closing ShellFlow will stop them. Your layout is saved.",
            "Close");
    }

    public async Task ShutdownAsync()
    {
        _statusTimer.Stop();
        _workspace.Save();
        _services.SaveSettingsNow();
        await Task.WhenAny(_workspace.UnloadAsync(), Task.Delay(TimeSpan.FromSeconds(4)));
        Log.Info("ShellFlow shut down.");
    }
}

internal static class AutomationPropertiesHelper
{
    public static void SetName(DependencyObject element, string name) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, name);
}
