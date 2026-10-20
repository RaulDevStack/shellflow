# Contributing to ShellFlow

Thanks for helping! ShellFlow aims to be a fast, reliable, beautiful terminal, so changes are judged on those three
things in that order.

## Getting started

1. Install the .NET 10 SDK on Windows 10 2004+ or Windows 11.
2. `dotnet build ShellFlow.slnx -p:Platform=x64`
3. Run `src/ShellFlow.App/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/ShellFlow.exe`.
   Set `SHELLFLOW_DATA_DIR` to a scratch folder to keep your real settings untouched.

## Where things go

- Logic that does not need a window (models, layout, persistence, validation, commands) belongs in `ShellFlow.Core`.
- Process and ConPTY code belongs in `ShellFlow.Terminal`.
- Anything that crosses the WebView2 boundary must go through `BridgeProtocol` with explicit validation. Never add a
  message that runs arbitrary commands, opens URLs or touches files.
- New user actions are added to `DefaultCommands` (Core) and bound in `MainView.RegisterCommands`, so the palette,
  shortcuts and menus share one implementation. Do not wire buttons to private handlers.
- Colors, radii and spacing come from `Themes/Tokens.xaml`. Do not hard-code hex values in views.

## Guidelines

- Keep the UI thread free: process I/O, file I/O and waits run off the UI thread.
- Dispose what you create (sessions, WebView2 controls, event subscriptions).
- Never swallow exceptions silently; log them (without user data) and show actionable messages.
- Do not log terminal input, output, clipboard contents or environment values.
- Respect reduced motion: animations must be optional and must never delay input or resizing.
- Prefer small, focused pull requests with a clear description of what changed and how you verified it. Include
  screenshots for visual changes (both themes when relevant).

## Commit messages

Use the imperative mood ("Add pane zoom", "Fix resize when tab is hidden") and explain *why* in the body when it is not
obvious.
