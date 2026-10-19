# ShellFlow architecture

ShellFlow is a WinUI 3 desktop application. Terminal emulation runs in xterm.js inside WebView2; shells run
natively behind the Windows Pseudo Console (ConPTY). Everything that can be tested without a window lives in
plain .NET libraries.

```
┌───────────────────────────── ShellFlow.App (WinUI 3) ─────────────────────────────┐
│ MainWindow ─ MainView ─ title bar · tab strip · toolbar · status bar · overlays    │
│        │                                                                          │
│ WorkspaceController ─▶ TabController ─▶ PaneController ──────┐                    │
│        │                     │ PaneHostPanel (split layout)  │                    │
│ AppServices: settings · profiles · theme · CommandRegistry   │                    │
└──────────────────────────────────────────────────────────────┼────────────────────┘
                                                               │
              ┌──────────── ShellFlow.TerminalHost ────────────┴──┐
              │ TerminalView (WebView2) ◀── validated messages ──▶ │  terminal.js + xterm.js
              │   output pump with ack-based flow control          │  (untrusted renderer)
              └───────────────────────────┬───────────────────────┘
                                          │ TerminalSession API
              ┌───────────── ShellFlow.Terminal ──────────────────┐
              │ ConPTY · CreateProcessW · pipes · job accounting  │──▶ powershell.exe / cmd.exe / bash / wsl
              └───────────────────────────────────────────────────┘
              ┌───────────── ShellFlow.Core (net10.0) ────────────┐
              │ split tree + layout · workspace/settings models   │
              │ persistence · profiles · commands · bridge schema │
              └───────────────────────────────────────────────────┘
```

## Projects

| Project | Target | Responsibility |
| --- | --- | --- |
| `ShellFlow.Core` | `net10.0` | Split-tree model (`PaneTree`, `PaneLayoutEngine`), workspace and settings models, validation and repair, versioned JSON persistence, shell profile detection and resolution, the command catalog and registry, key chords, fuzzy matching, and the renderer bridge schema (`BridgeProtocol`). No WinUI dependency. |
| `ShellFlow.Terminal` | `net10.0-windows` | `TerminalSession`: creates the pseudo console and the shell process, streams output on a dedicated reader thread through a bounded channel, writes input on a dedicated writer thread, resizes, detects exit, and closes gracefully. Uses a job object for process accounting (is a command running?). |
| `ShellFlow.TerminalHost` | WinUI 3 library | `TerminalView`: hosts one xterm.js renderer in WebView2, maps bundled assets to a virtual host, validates every renderer message, and pumps output with flow control. Also owns the shared WebView2 environment. |
| `ShellFlow.App` | WinUI 3 app (unpackaged) | Window, chrome, tabs, split panes, command palette, find, settings, workspaces, theme and motion. |
| `ShellFlow.Tests` | `net10.0-windows` | xUnit tests for Core logic and ConPTY integration tests that start real `cmd.exe` and `powershell.exe` processes. |

## Data flow

**Startup.** `App.OnLaunched` loads settings (`SettingsStore`), detects shells (`ProfileCatalog`), configures the
shared WebView2 environment and opens `MainWindow`. `MainView.StartAsync` always starts clean: one tab with a
single terminal using the default shell. It keeps the last workspace's name but discards its previous tabs and splits.
Saved workspaces are reopened only when the user switches to one, and are then repaired with `WorkspaceValidator`
(unknown profiles fall back to the default shell, broken layouts are reset).

**Opening a pane.** `TabController` creates a `PaneController`, which creates a `TerminalView` and loads
`https://terminal.shellflow.example/index.html` (a virtual host mapped to the bundled files). The renderer measures its
grid and sends `ready {cols, rows}`. Only then does the pane resolve its profile and start the shell with exactly that
size, so the first prompt is laid out correctly.

**Output.** The ConPTY reader thread decodes UTF-8 (stateful, so split multi-byte sequences are handled) into a
bounded channel of 64 chunks. `TerminalView`'s pump coalesces chunks (up to 128 K characters per message) and posts them
to the renderer as plain strings, keeping only one post in flight: while the UI thread is busy, output accumulates and the
next message carries all of it, so the per-message IPC cost does not grow with output volume. The renderer acknowledges what it has parsed; when more than 512 K characters are
unacknowledged the pump waits. A full channel blocks the reader thread, which makes ConPTY stop reading from the shell —
memory stays bounded and a flood of output cannot freeze the UI.

**Input.** xterm.js produces input (`onData`), which is posted as `input {data}` (chunked to 32 K characters), validated,
and queued to the session's writer thread. The UI thread never blocks on pipe I/O.

**Resizing.** Layout changes resize WebView2; a `ResizeObserver` in the renderer calls the fit addon (once per
animation frame), xterm.js reports `resize {cols, rows}`, and native code calls `ResizePseudoConsole`. The renderer is
the single source of truth for the grid size, so the shell and the renderer always agree.

**Commands.** Every action (toolbar, menus, keyboard, palette, renderer shortcuts) is a `CommandDefinition` in
`DefaultCommands` with a handler bound in `MainView.RegisterCommands`. When a terminal has focus, keys never reach XAML,
so the renderer receives the shortcut table and posts `command {id}` for bound chords; native code checks the id
against the registry before running it.

**Layout.** A tab's panes form a binary split tree (`PaneLeaf` / `PaneSplit` with an arrangement and ratio).
`PaneHostPanel` computes rectangles with `PaneLayoutEngine` (respecting minimum pane sizes for whole subtrees) and only
re-arranges existing pane elements — renderers and shells are never recreated by splits, closes or resizes.

**Tabs.** Inactive tabs are hidden with opacity 0 rather than collapsed. A collapsed WebView2 is treated as a hidden page
and Chromium throttles its timers, which would slow xterm.js's write loop and, through flow control, the shells running
in background tabs.

**Persistence.** Settings, state and workspaces are JSON under `%LOCALAPPDATA%\ShellFlow` (override with
`SHELLFLOW_DATA_DIR`). Files carry a `schemaVersion`; writes are atomic (temp file + replace); unreadable files are moved
aside as `*.corrupt-<timestamp>.json` and defaults are used; files from a newer version are read best-effort and never
overwritten. Workspaces autosave 1.5 s after structural changes and on exit.

**Shutdown.** Closing the window asks for confirmation if commands are running, saves the workspace, settings and window
placement, then closes every session: `ClosePseudoConsole` signals console clients (`CTRL_CLOSE_EVENT`), the shell is
given two seconds to exit, then terminated. GUI programs started from a shell are deliberately not killed.

## Key decisions

- **WebView2 + xterm.js** for terminal emulation instead of a hand-written VT parser: mature escape-sequence support,
  Unicode widths, search, WebGL rendering.
- **One WebView2 per pane, one shared environment.** All panes share a browser process; each pane has an isolated page,
  so a renderer crash affects one pane and is recovered by reloading (the shell keeps running).
- **No reflection-based JSON.** `System.Text.Json` source generation keeps startup fast and the schema explicit.
- **Unpackaged, self-contained Windows App SDK.** No MSIX install or framework package required; the portable build runs
  from any folder.
- **Job object for accounting only.** "Is a command running?" compares live processes in the shell's job against an idle
  baseline measured shortly after start (some shells, like Git Bash and WSL, keep helper processes). It is a heuristic and
  cannot see work done inside the shell process itself.

## Repository layout

```
src/ShellFlow.Core/          models, layout, persistence, profiles, commands, bridge schema
src/ShellFlow.Terminal/      ConPTY session
src/ShellFlow.TerminalHost/  WebView2 host + Assets/terminal (index.html, terminal.js, vendored xterm.js)
src/ShellFlow.App/           WinUI app (Themes/, Controls/, Workspace/, Services/)
tests/ShellFlow.Tests/       unit + ConPTY integration tests
tests/e2e/cdp.mjs            drives running renderers over the DevTools protocol (manual/automated UI checks)
web/                         npm project that vendors xterm.js into TerminalHost (not needed to build)
build/                       icon generation and release packaging scripts
docs/                        architecture, security, testing, design notes
```
