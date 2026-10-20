namespace ShellFlow.Core.Commands;

/// <summary>
/// The catalog of built-in commands: titles, categories, default shortcuts and icons. The UI attaches handlers
/// by id. Keeping the catalog here lets tests verify that every default shortcut parses and none conflict.
/// </summary>
public static class DefaultCommands
{
    public static IReadOnlyList<CommandDefinition> All { get; } =
    [
        new(CommandIds.NewTab, "New tab", "Tabs", "Ctrl+Shift+T", "") { Keywords = "terminal open shell" },
        new(CommandIds.NewTerminalWithProfile, "New terminal with shell…", "Tabs", "Ctrl+Shift+Space", "") { Keywords = "profile powershell cmd bash wsl" },
        new(CommandIds.CloseTab, "Close tab", "Tabs", "Ctrl+F4", ""),
        new(CommandIds.NextTab, "Next tab", "Tabs", "Ctrl+Tab | Ctrl+PageDown", ""),
        new(CommandIds.PreviousTab, "Previous tab", "Tabs", "Ctrl+Shift+Tab | Ctrl+PageUp", ""),
        new(CommandIds.RenameTab, "Rename tab…", "Tabs", null, ""),

        new(CommandIds.SplitRight, "Split right", "Panes", "Ctrl+Shift+E | Alt+Shift+Equal", "") { Keywords = "vertical side by side" },
        new(CommandIds.SplitDown, "Split down", "Panes", "Ctrl+Shift+O | Alt+Shift+Minus", "") { Keywords = "horizontal stacked" },
        new(CommandIds.ClosePane, "Close pane", "Panes", "Ctrl+Shift+W", ""),
        new(CommandIds.FocusNextPane, "Focus next pane", "Panes", "Ctrl+Shift+BracketRight", ""),
        new(CommandIds.FocusPreviousPane, "Focus previous pane", "Panes", "Ctrl+Shift+BracketLeft", ""),
        new(CommandIds.FocusLeft, "Focus pane left", "Panes", "Alt+Left", ""),
        new(CommandIds.FocusRight, "Focus pane right", "Panes", "Alt+Right", ""),
        new(CommandIds.FocusUp, "Focus pane above", "Panes", "Alt+Up", ""),
        new(CommandIds.FocusDown, "Focus pane below", "Panes", "Alt+Down", ""),
        new(CommandIds.ResizeLeft, "Move divider left", "Panes", "Alt+Shift+Left", "") { Keywords = "resize" },
        new(CommandIds.ResizeRight, "Move divider right", "Panes", "Alt+Shift+Right", "") { Keywords = "resize" },
        new(CommandIds.ResizeUp, "Move divider up", "Panes", "Alt+Shift+Up", "") { Keywords = "resize" },
        new(CommandIds.ResizeDown, "Move divider down", "Panes", "Alt+Shift+Down", "") { Keywords = "resize" },
        new(CommandIds.EqualizePanes, "Equalize pane sizes", "Panes", null, "") { Keywords = "balance even reset" },
        new(CommandIds.RestartPane, "Restart shell", "Panes", null, "") { Keywords = "relaunch reload" },

        new(CommandIds.Copy, "Copy", "Terminal", "Ctrl+Shift+C", ""),
        new(CommandIds.Paste, "Paste", "Terminal", "Ctrl+Shift+V | Ctrl+V | Shift+Insert", ""),
        new(CommandIds.Search, "Find in terminal", "Terminal", "Ctrl+Shift+F", "") { Keywords = "search" },
        new(CommandIds.SelectAll, "Select all", "Terminal", "Ctrl+Shift+A", ""),
        new(CommandIds.ClearTerminal, "Clear scrollback", "Terminal", null, "") { Keywords = "clear reset buffer" },
        new(CommandIds.FontIncrease, "Increase font size", "Terminal", "Ctrl+Equal", "") { Keywords = "zoom in bigger" },
        new(CommandIds.FontDecrease, "Decrease font size", "Terminal", "Ctrl+Minus", "") { Keywords = "zoom out smaller" },
        new(CommandIds.FontReset, "Reset font size", "Terminal", "Ctrl+0", "") { Keywords = "zoom" },

        new(CommandIds.CommandPalette, "Command palette", "General", "Ctrl+Shift+P", ""),
        new(CommandIds.OpenSettings, "Settings", "General", "Ctrl+Comma", "") { Keywords = "preferences options configure" },
        new(CommandIds.ToggleTheme, "Toggle light / dark theme", "General", null, "") { Keywords = "change theme appearance" },
        new(CommandIds.ShowShortcuts, "Keyboard shortcuts", "General", "Ctrl+Shift+Slash", "") { Keywords = "keys bindings help" },

        new(CommandIds.SwitchWorkspace, "Switch workspace…", "Workspaces", "Ctrl+Shift+K", "") { Keywords = "open restore" },
        new(CommandIds.NewWorkspace, "New workspace…", "Workspaces", null, ""),
        new(CommandIds.RenameWorkspace, "Rename workspace…", "Workspaces", null, ""),
        new(CommandIds.SaveWorkspace, "Save workspace", "Workspaces", null, ""),
        new(CommandIds.DeleteWorkspace, "Delete workspace…", "Workspaces", null, ""),
    ];

    public static CommandDefinition Get(string id) =>
        All.FirstOrDefault(d => d.Id == id) ?? throw new KeyNotFoundException($"No built-in command '{id}'.");
}
