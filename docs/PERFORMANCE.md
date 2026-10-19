# Performance baseline

Numbers below are measurements, not targets. Always report the conditions with any new numbers; results vary a lot
between machines, shells and display settings.

## How to measure

```powershell
.\build\publish.ps1 -SkipTests      # Release, self-contained, ReadyToRun
.\build\measure.ps1                 # starts an isolated instance and measures it
```

`measure.ps1` launches ShellFlow with a fresh data folder and a private DevTools port, waits for the first PowerShell
prompt to appear in the renderer, then types `1..100000 | ForEach-Object { 'line ' + $_ }` and waits until the last line
is rendered. It polls the renderer through `tests/e2e/cdp.mjs` (each poll starts a Node.js process, adding up to roughly
100 ms of latency to the timings).

The ConPTY-only comparison uses a small console harness that starts the same shell through `TerminalSession` and only
drains the output channel (no renderer).

## Results — 2026-10-18

| | |
| --- | --- |
| Build | 0.1.0, Release, win-x64, self-contained, ReadyToRun (`artifacts/publish/win-x64`) |
| Machine | Intel Core i9-12900KF, Windows 11 build 26300 |
| Shell | Windows PowerShell 5.1, default profile, 176 × 39 grid |
| Renderer | WebGL |

| Measurement | Result |
| --- | --- |
| Process start → first prompt rendered | 1.10 s (second run; first run after publish 1.09–1.72 s) |
| 100 000 lines through ShellFlow (ConPTY → WebView2 → xterm.js) | 11.6 s |
| 100 000 lines through ConPTY only (no renderer) | 11.5 s |
| 20 000 lines through ConPTY only | 2.2 s |
| ShellFlow process working set after the run (1 pane) | ~170–190 MB |
| WebView2 processes working set (1 pane) | ~350–460 MB |

**Reading the results.** Rendering adds about 1 % on top of ConPTY for this workload: Windows PowerShell's own output
speed is the bottleneck, not the renderer. Output batching keeps one WebView2 message in flight at a time; while the UI
thread is busy, further output is coalesced into the next message, so message count stays low under load.

## Not yet measured

- Keystroke-to-echo latency (needs a high-speed camera or an input-latency tool).
- Behavior with many panes (10+), memory growth over long sessions, and output with heavy escape-sequence use
  (e.g. full-screen TUIs).
- Startup on a cold machine (first launch after boot), ARM64 devices, and Windows 10.
