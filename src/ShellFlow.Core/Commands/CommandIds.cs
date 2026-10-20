namespace ShellFlow.Core.Commands;

/// <summary>Stable command identifiers. They appear in settings.json key-binding overrides, so do not rename them.</summary>
public static class CommandIds
{
    public const string NewTab = "tab.new";
    public const string NewTerminalWithProfile = "tab.newWithProfile";
    public const string CloseTab = "tab.close";
    public const string NextTab = "tab.next";
    public const string PreviousTab = "tab.previous";
    public const string RenameTab = "tab.rename";

    public const string SplitRight = "pane.splitRight";
    public const string SplitDown = "pane.splitDown";
    public const string ClosePane = "pane.close";
    public const string FocusNextPane = "pane.focusNext";
    public const string FocusPreviousPane = "pane.focusPrevious";
    public const string FocusLeft = "pane.focusLeft";
    public const string FocusRight = "pane.focusRight";
    public const string FocusUp = "pane.focusUp";
    public const string FocusDown = "pane.focusDown";
    public const string ResizeLeft = "pane.resizeLeft";
    public const string ResizeRight = "pane.resizeRight";
    public const string ResizeUp = "pane.resizeUp";
    public const string ResizeDown = "pane.resizeDown";
    public const string EqualizePanes = "pane.equalize";
    public const string RestartPane = "pane.restart";

    public const string Copy = "terminal.copy";
    public const string Paste = "terminal.paste";
    public const string Search = "terminal.search";
    public const string ClearTerminal = "terminal.clear";
    public const string SelectAll = "terminal.selectAll";
    public const string FontIncrease = "terminal.fontIncrease";
    public const string FontDecrease = "terminal.fontDecrease";
    public const string FontReset = "terminal.fontReset";

    public const string CommandPalette = "app.commandPalette";
    public const string OpenSettings = "app.settings";
    public const string ToggleTheme = "app.toggleTheme";
    public const string ShowShortcuts = "app.shortcuts";

    public const string SwitchWorkspace = "workspace.switch";
    public const string NewWorkspace = "workspace.new";
    public const string RenameWorkspace = "workspace.rename";
    public const string SaveWorkspace = "workspace.save";
    public const string DeleteWorkspace = "workspace.delete";
}
