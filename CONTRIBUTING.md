# Contributing to ShellFlow

Thanks for helping! ShellFlow aims to be a fast, reliable, beautiful terminal, so changes are judged on those three
things in that order.

## Getting started

1. Install the .NET 10 SDK on Windows 10 2004+ or Windows 11.
2. `dotnet build ShellFlow.slnx -p:Platform=x64`
3. `dotnet test tests/ShellFlow.Tests`
4. Run `src/ShellFlow.App/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/ShellFlow.exe`.
   Set `SHELLFLOW_DATA_DIR` to a scratch folder to keep your real settings untouched.

## Where things go

- Logic that does not need a window (models, layout, persistence, validation, commands) belongs in `ShellFlow.Core`
  and should come with unit tests.
- Process and ConPTY code belongs in `ShellFlow.Terminal`; add an integration test in `TerminalSessionTests` when you
  change behavior.
- Anything that crosses the WebView2 boundary must go through `BridgeProtocol` with explicit validation. Read
  [docs/SECURITY.md](docs/SECURITY.md) before adding a message type; never add a message that runs arbitrary commands,
  opens URLs or touches files.
- New user actions are added to `DefaultCommands` (Core) and bound in `MainView.RegisterCommands`, so the palette,
  shortcuts and menus share one implementation. Do not wire buttons to private handlers.
- Colors, radii and spacing come from `Themes/Tokens.xaml`. Do not hard-code hex values in views.

## Guidelines

- Keep the UI thread free: process I/O, file I/O and waits run off the UI thread.
- Dispose what you create (sessions, WebView2 controls, event subscriptions).
- Never swallow exceptions silently; log them (without user data) and show actionable messages.
- Do not log terminal input, output, clipboard contents or environment values.
- Respect reduced motion: animations must be optional and must never delay input or resizing.
- Prefer small, focused pull requests with a clear description of what changed and how you verified it.
- Run `dotnet test` and, for UI changes, the relevant items of the manual checklist in [docs/TESTING.md](docs/TESTING.md).
  Include screenshots for visual changes (both themes when relevant).

## Updating xterm.js

```powershell
cd web
npm install @xterm/xterm@<version> ...   # update package.json pins
npm run vendor                           # copies dist files + license notices into TerminalHost
```

Review the upstream changelog and licenses, then test the manual checklist.

## Releasing

1. Update `<Version>` in `Directory.Build.props`, move the `[Unreleased]` entries in `CHANGELOG.md` under the new
   version, and add `docs/release-notes/v<version>.md`.
2. Commit, then tag and push: `git tag v<version>` and `git push origin v<version>`.
3. The **Release** workflow tests, builds the MSI and the portable zip, writes `SHA256SUMS.txt` and publishes the
   GitHub release. `build\release.ps1` produces the same assets locally in `artifacts\release`.

## Commit messages

Use the imperative mood ("Add pane zoom", "Fix resize when tab is hidden") and explain *why* in the body when it is not
obvious.
