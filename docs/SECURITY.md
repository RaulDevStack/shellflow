# Security model

ShellFlow runs the commands you type in the shell you chose. Its job is to make sure nothing *else* can run commands
or reach native code through it. The terminal renderer (JavaScript in WebView2) is treated as untrusted: it displays
output produced by arbitrary programs, and escape sequences in that output are attacker-controlled input.

## Trust boundaries

| Component | Trust | Notes |
| --- | --- | --- |
| Native app (C#) | Trusted | Creates processes, owns files and the clipboard. |
| Shell processes | User's own processes | Started only from a resolved profile executable, never from renderer input. |
| Renderer (terminal.js + xterm.js in WebView2) | **Untrusted** | Renders program output. May only talk to native code through the bridge below. |
| Settings / workspace JSON | Semi-trusted | User-editable; validated and repaired on load. |

## The bridge

All renderer→native traffic goes through `chrome.webview.postMessage` and is parsed by
`ShellFlow.Core.Bridge.BridgeProtocol` before anything acts on it.

- **Origin check.** Messages are accepted only when `WebMessageReceived.Source` is exactly
  `https://terminal.shellflow.example` (default port). Anything else is dropped.
- **Closed set of message types**: `ready`, `input`, `resize`, `ack`, `command`, `copy`, `title`, `cwd`, `focus`,
  `searchResult`, `renderer`, `error`, `dirList`, `dirAccept`. Unknown types are rejected.
- **cd folder picker.** `dirList` returns only the *names* of folders (hidden and system folders excluded, at most 500,
  2-second limit) for a path; the renderer cannot read files, and it has no network access to send the names anywhere.
  `dirAccept` types `cd <folder>` into the pane's own shell — something the renderer could already do with `input` —
  after native code checks the folder exists and quotes it for that shell (PowerShell single quotes, so `$` is never
  expanded).
- **Strict field validation.** Every field is type-checked; numbers are range-checked (columns 1–2000, rows 1–1000);
  strings have maximum lengths (input 64 K characters per message, copy 4 M, title 256, path 4096); the whole message
  is capped at 6 MB and JSON depth at 4.
- **No generic capability.** There is no message to start processes, open URLs, read or write files, or call arbitrary
  native methods. `input` goes only to the pane's own, already-running shell. `command` carries an id matching
  `^[a-z]{2,16}\.[A-Za-z0-9]{2,40}$`, which native code looks up in the command registry; unknown ids are ignored.
  `cwd` is only displayed and used as a starting-directory hint for new panes after native code checks the directory exists.
- **Control characters** are stripped from titles and rejected in paths, so a malicious OSC title cannot inject
  content into the native UI.
- **Logging never includes message contents** (keystrokes, clipboard data or output); rejections are logged by reason only.

Native→renderer messages are plain strings (shell output) or typed JSON produced by source-generated serializers.

## WebView2 hardening

- Content is served from the application folder through `SetVirtualHostNameToFolderMapping` on a reserved `.example`
  host (never resolvable on the network). Navigation to any other URL is cancelled; iframes are blocked; new windows,
  downloads and permission requests are denied.
- A Content Security Policy allows scripts and styles only from the bundled files (`'unsafe-inline'` is needed for
  styles because xterm.js positions rows with inline styles) and blocks network access (`connect-src 'none'`), frames,
  objects and forms.
- Disabled: host objects, default context menus, script dialogs, browser accelerator keys (Ctrl+F, F5, …),
  status bar, zoom, swipe navigation, autofill and password saving. DevTools are available only in Debug builds or when
  `SHELLFLOW_DEVTOOLS=1` is set.
- Links printed in the terminal are not clickable (no link addon), so the renderer never opens URLs.
- The WebView2 user data folder is private to ShellFlow (`%LOCALAPPDATA%\ShellFlow\WebView2`).

> WebView2 also honours the standard `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS` environment variable (for example to enable
> remote debugging). That is a property of the machine's environment, not something renderer content can set.

## Clipboard

Copy and paste are performed natively. The renderer can request a copy (selected text), but paste is initiated only by
a user action (shortcut, right-click, menu) and runs through `terminal.paste`, which reads the clipboard natively and can
warn before pasting multiple lines (enabled by default). xterm.js applies bracketed-paste mode when the shell requests it.

## Single instance

A second launch forwards its command line to the running window over a named pipe created with
`PipeOptions.CurrentUserOnly`: only processes of the same Windows user can connect, and the client verifies the server
belongs to that user too. The pipe and its guarding mutex are named after a hash of the user's SID and the data folder.
Messages are length-prefixed (64 KB max) JSON arrays of strings, parsed with the same `StartupArguments` parser as the
real command line, so the only things a forwarded launch can request are a folder, a shell profile id and new tab / new
pane — never a command to run.

## Shell integration

To learn the current folder, ShellFlow passes PowerShell an `-EncodedCommand` that wraps the existing `prompt`
function (the script is a constant in `ShellIntegration.cs`), and sets `PROMPT` (CMD) or `PROMPT_COMMAND` (Git Bash) for
the shell it starts. Nothing is written to the user's profile files, and it can be turned off in Settings.

## Process lifecycle

- Executables come only from profiles (detected or user-defined) and are resolved to absolute paths before launch.
  Relative paths containing directories are refused.
- Children inherit the user's environment plus `TERM_PROGRAM=ShellFlow`, `TERM_PROGRAM_VERSION` and `COLORTERM`.
  Environment overrides from profiles are validated (no `=` or NUL in names).
- Standard handles are not inherited (`STARTF_USESTDHANDLES` with no handles, `bInheritHandles = FALSE`), so a shell
  cannot write around the pseudo console into ShellFlow's own handles.

## Dependencies

| Dependency | Version | License | Use |
| --- | --- | --- | --- |
| @xterm/xterm | 6.0.0 | MIT | Terminal emulation and rendering |
| @xterm/addon-fit | 0.11.0 | MIT | Size the grid to the pane |
| @xterm/addon-search | 0.16.0 | MIT | Find in terminal |
| @xterm/addon-unicode11 | 0.9.0 | MIT | Unicode 11 character widths |
| @xterm/addon-webgl | 0.19.0 | MIT | GPU rendering (falls back to DOM) |
| Microsoft.WindowsAppSDK | 1.8.260921001 | MIT | WinUI 3, windowing |
| Microsoft.Windows.SDK.BuildTools | 10.0.28000.2705 | MIT | Build-time Windows SDK tools |
| Microsoft.Web.WebView2 (via Windows App SDK) | transitive | See the package's license | WebView2 projection |

The xterm.js files are vendored in `src/ShellFlow.TerminalHost/Assets/terminal/vendor` together with their license
texts (`THIRD-PARTY-NOTICES.txt`); `web/package.json` pins the exact versions and `npm run vendor` regenerates them.

## Reporting a vulnerability

Please report security issues privately through
[GitHub private vulnerability reporting](https://github.com/RaulDevStack/shellflow/security/advisories/new) instead of
opening a public issue. Include the ShellFlow version, Windows build and steps to reproduce. You will get a reply as soon
as possible, and fixes are credited in the release notes unless you prefer otherwise.
