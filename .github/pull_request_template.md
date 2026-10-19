## What and why

<!-- What does this change, and why is it needed? Link related issues. -->

## How it was verified

- [ ] `dotnet build src/ShellFlow.App -c Release -p:Platform=x64` succeeds
- [ ] Tested the change in the running app on Windows (describe what you checked)
- [ ] Screenshots for visual changes (dark and light theme if affected)

## Checklist

- [ ] New actions go through `DefaultCommands` / the command registry
- [ ] No new renderer bridge capability, or it is validated in `BridgeProtocol`
- [ ] No user data (input, output, clipboard, environment) in logs
