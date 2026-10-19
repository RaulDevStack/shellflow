# Changelog

All notable changes to ShellFlow are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/)
and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.1.0] - 2026-10-19

First public preview.

### Added
- ConPTY terminal sessions with bounded, backpressured output streaming, graceful shutdown and job-based
  "command running" detection.
- xterm.js renderer in WebView2 with WebGL (DOM fallback), Unicode 11 widths, search, copy/paste and bracketed paste.
- Validated renderer bridge with origin checks, strict message schema and no generic native capabilities.
- Tabs with drag reordering; nested split panes with draggable dividers, keyboard focus movement and resizing.
- Command registry shared by toolbar, menus, shortcuts and the command palette (`Ctrl+Shift+P`).
- Shell detection for PowerShell 7, Windows PowerShell, Command Prompt, Git Bash and WSL; custom shells.
- Settings UI (general, appearance, terminal, shells, shortcuts, about) with live apply and persistence.
- Workspaces: create, rename, switch, delete and autosave.

- Hyprland-inspired "Fluid" animation style (default): window and pane pop-in, gliding layout changes, pane exit
  animation, sliding tab switches, palette pop-in and a gradient border on the active pane. "Subtle" keeps short fades.

- Per-user MSI installer (WiX 5): Start menu shortcut, "Open ShellFlow here" for folders, folder backgrounds and
  drives, `shellflow` in Win+R, clean uninstall, in-place upgrades.
- Command-line options `-d/--directory` and `-p/--profile` (also accepts a bare folder path).

- Shell integration for PowerShell, Command Prompt and Git Bash (current folder via OSC 9;9); new panes open in the
  selected pane's current folder.
- Explorer entries "Open ShellFlow here (new tab)" and "(new pane)"; single instance — later launches reuse the open
  window (`--new-tab`, `--new-pane`).
- cd folder picker: `cd` + Tab opens a keyboard-driven folder browser with filtering, drive list and shell-correct quoting.

### Fixed
- Splitting is refused (with a hint) when the resulting panes would be smaller than the minimum size, so panes no longer
  collapse into slivers or disappear; "Open ShellFlow here (new pane)" opens a tab when the current one is full.
- With many tabs the tab strip overflowed under the toolbar and the window's caption buttons, which then stopped
  responding. Tabs now shrink to share the space, scroll (mouse wheel, selected tab kept visible), and title-bar
  click-through areas are clipped so minimize / maximize / close always work.
- Ctrl+C was ignored by every shell when ShellFlow had been started by a process with Ctrl+C disabled (the flag is
  inherited); ShellFlow now restores normal Ctrl+C processing before starting shells.

### Changed
- ShellFlow always starts with a single terminal; the previous session's tabs and splits are discarded on launch.
  The "Restore the last workspace on startup" setting was removed (old settings files that contain it still load).
- Dark and light themes, four accents, motion that follows the Windows animation setting.
- Corrupt-configuration recovery, newer-version protection and atomic saves.
- Unit tests and ConPTY integration tests; CDP-based UI driver; portable release script.
