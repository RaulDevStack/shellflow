## What and why

<!-- What does this change, and why is it needed? Link related issues. -->

## How it was verified

- [ ] `dotnet test tests/ShellFlow.Tests` passes
- [ ] Relevant items from docs/TESTING.md checked on Windows (list them)
- [ ] Screenshots for visual changes (dark and light theme if affected)

## Checklist

- [ ] New actions go through `DefaultCommands` / the command registry
- [ ] No new renderer bridge capability, or it is validated and documented in docs/SECURITY.md
- [ ] No user data (input, output, clipboard, environment) in logs
