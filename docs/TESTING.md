# Testing ShellFlow

## Automated tests

```powershell
dotnet test tests/ShellFlow.Tests
```

The suite has two kinds of tests:

- **Unit tests** for Core: split tree operations and geometry, layout with minimum sizes, workspace and settings
  serialization, corruption recovery and newer-version protection, workspace repair on restore, key chord parsing,
  the command registry (aliases, overrides, conflicts, failure reporting), the default command catalog, fuzzy matching,
  bridge message validation (including hostile inputs), profile detection on simulated machines, executable resolution,
  and environment block construction.
- **ConPTY integration tests** (`TerminalSessionTests`) that start real `cmd.exe` and Windows PowerShell processes:
  command output, ANSI color sequences, exit codes, Ctrl+C interrupting a running command, resize visible to the shell,
  UTF-8 round trip, 5 000 lines of output, idempotent close, repeated open/close without leaked processes, missing
  executables, and missing working directories. They require Windows 10 1809+ and take about 20 seconds.

## Driving the real UI

`tests/e2e/cdp.mjs` talks to running terminal renderers over the Chrome DevTools Protocol, which is useful for
repeatable end-to-end checks.

```powershell
$env:SHELLFLOW_DATA_DIR = "$env:TEMP\shellflow-e2e"          # keep test state separate
$env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=9333"
.\src\ShellFlow.App\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\ShellFlow.exe

node tests/e2e/cdp.mjs list                                   # one entry per pane
node tests/e2e/cdp.mjs type 0 'Get-Location\r'                # real key input into pane 0
node tests/e2e/cdp.mjs key 0 KeyE E 69 10                     # Ctrl+Shift+E (split right)
node tests/e2e/cdp.mjs text 0                                 # read pane 0's buffer
```

Only use the debugging port on a development machine.

## Manual verification checklist (Windows)

Record the Windows build, display scaling and ShellFlow commit when filing results.

**Terminal basics**
- [ ] App opens to a PowerShell prompt; the status bar shows *Ready*, the shell name, "started in …" and the grid size.
- [ ] `Get-Location`, `Get-ChildItem`, `Write-Host "ShellFlow" -ForegroundColor Cyan` print correctly with colors.
- [ ] `ping -t 127.0.0.1` then Ctrl+C stops it and returns to the prompt; the status bar shows *Running command* meanwhile.
- [ ] `Read-Host "Name"` accepts input; arrow keys, Home/End, Backspace and history (Up) work in PSReadLine.
- [ ] `echo "héllo ✓ 日本語 🚀"` renders correctly; wide characters occupy two cells.
- [ ] `1..20000` scrolls smoothly; the UI stays responsive; scrollback can be scrolled with the mouse wheel.
- [ ] Resizing the window reflows text; `$Host.UI.RawUI.WindowSize` matches the status bar size.
- [ ] Select text and press Ctrl+C (copies, does not interrupt); Ctrl+C without a selection interrupts.
- [ ] Ctrl+V / Ctrl+Shift+V / Shift+Insert / right-click paste; multi-line paste shows a warning.
- [ ] `exit` closes the pane (when "Close panes when the shell exits successfully" is on); `exit 3` keeps it with an
      "exited with code 3" banner, and Enter restarts the shell.

**Tabs and panes**
- [ ] Ctrl+Shift+T opens a tab; Ctrl+Tab / Ctrl+Shift+Tab cycle; tabs can be reordered by dragging.
- [ ] Ctrl+Shift+E splits right, Ctrl+Shift+O splits down, repeatedly and nested; each pane runs its own shell.
- [ ] Alt+Arrows move focus geometrically; Alt+Shift+Arrows move the nearest divider; dividers can be dragged and
      double-clicked to equalize.
- [ ] Panes never shrink below a usable size; shells receive the new size after every layout change.
- [ ] Ctrl+Shift+W closes the focused pane only; closing a pane with a running command asks first.
- [ ] Closing the last pane of the last tab shows the empty state with a *New tab* button.

**Productivity**
- [ ] Ctrl+Shift+P opens the palette from a terminal; typing filters; arrows + Enter run commands; Esc closes.
- [ ] Ctrl+Shift+Space lists detected shells (PowerShell, Command Prompt, Git Bash/WSL only if installed).
- [ ] Ctrl+Shift+F finds text, shows "n of m", Enter/Shift+Enter navigate, options toggle; Esc returns focus.
- [ ] Settings (Ctrl+,): change theme, accent, font, size, cursor, scrollback — terminals update live; restart ShellFlow
      and verify the values persisted.
- [ ] Workspaces: create, rename, switch (running commands prompt first), delete with confirmation.
- [ ] Open several tabs and splits, close ShellFlow, reopen it: it starts with exactly one tab and one terminal.

**Folders and launching**
- [ ] In a pane, `cd C:\Windows`, then split: the new pane starts in `C:\Windows` (also for CMD and Git Bash).
- [ ] Type `cd Win` + Tab in `C:\Program Files`: the picker lists matching folders; ↑↓ select, Tab/→ open, ← goes up and
      keeps the previous folder selected, ← at a drive root shows the drives, Enter runs `cd` with correct quoting, Esc closes.
- [ ] In a folder with no reported directory (e.g. integration off), `cd src` + Tab falls back to the shell's own completion.
- [ ] With ShellFlow open, Explorer → right-click a folder → *Open ShellFlow here (new pane)*: a pane is added to the
      current tab in that folder; *(new tab)* opens a tab. Task Manager shows a single ShellFlow process.
- [ ] Start ShellFlow from the Start menu while it is open: the existing window comes to the front.

**Robustness**
- [ ] Corrupt `%LOCALAPPDATA%\ShellFlow\settings.json` (e.g. truncate it): ShellFlow starts with defaults, shows a notice,
      and keeps the old file as `settings.corrupt-*.json`.
- [ ] Add a profile pointing to a missing executable: opening it shows an actionable error in the pane; other panes work.
- [ ] Close the window with a running command: confirmation appears; after closing, no `powershell.exe`/`cmd.exe`
      started by ShellFlow remains (check Task Manager).

**Accessibility and display**
- [ ] All toolbar buttons have tooltips and accessible names (Narrator / Accessibility Insights).
- [ ] Tab order through the title bar, tabs and toolbar is logical; focus rectangles are visible.
- [ ] At 100 %, 150 % and 200 % scaling the chrome stays crisp and aligned; terminals stay sharp.
- [ ] With Windows *Animation effects* off (or Settings → Animations → Off) panels appear without motion.
- [ ] Light theme and dark theme both have readable text and visible focus/selection states.
