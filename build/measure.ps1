<#
.SYNOPSIS
  Records a simple performance baseline for a ShellFlow build: time from process start until the first shell
  prompt is rendered, and time to stream a large amount of output through ConPTY → WebView2 → xterm.js.
.NOTES
  Requires Node.js (for tests/e2e/cdp.mjs). Uses an isolated data folder and its own DevTools port.
  Results depend heavily on the machine; always report the test conditions alongside the numbers.
#>
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\artifacts\publish\win-x64\ShellFlow.exe'),
    [int]$Port = 9334,
    [int]$Lines = 100000
)

# 'Continue': node writes connection errors to stderr while the app is still starting, which is expected here.
$ErrorActionPreference = 'Continue'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$cdp = Join-Path $root 'tests\e2e\cdp.mjs'
$data = Join-Path ([IO.Path]::GetTempPath()) "shellflow-measure-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory -Force $data | Out-Null

$env:SHELLFLOW_DATA_DIR = $data
$env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$Port"
$env:CDP_PORT = "$Port"

function Get-Buffer { (cmd /c "node `"$cdp`" tail 0 60 2>nul") -join "`n" }

$sw = [Diagnostics.Stopwatch]::StartNew()
$process = Start-Process -FilePath $Exe -PassThru
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ((Get-Buffer) -match 'PS [A-Z]:\\.*>') { break }
        Start-Sleep -Milliseconds 50
    }
    $startup = $sw.Elapsed.TotalMilliseconds

    # Stream output generated inside the shell and wait until the last line is rendered.
    $sw.Restart()
    # Single quotes only: Windows PowerShell 5.1 strips embedded double quotes from native command arguments.
    node $cdp type 0 "1..$Lines | ForEach-Object { 'line ' + `$_ }; 'measure-' + 'done'\r" | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ((Get-Buffer) -match 'measure-done') { break }
        Start-Sleep -Milliseconds 100
    }
    $stream = $sw.Elapsed.TotalMilliseconds
    $tail = (Get-Buffer) -split "`n" | Where-Object { $_.Trim() } | Select-Object -Last 3

    $memory = [Math]::Round(((Get-Process -Id $process.Id).WorkingSet64 / 1MB), 0)
    $webview = (Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" | Where-Object { $_.CommandLine -like "*$data*" } |
        ForEach-Object { (Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue).WorkingSet64 } | Measure-Object -Sum).Sum / 1MB

    [pscustomobject]@{
        Build = $Exe
        Windows = [Environment]::OSVersion.Version.ToString()
        CPU = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name.Trim()
        StartupToPromptMs = [Math]::Round($startup)
        StreamLines = $Lines
        StreamMs = [Math]::Round($stream)
        AppWorkingSetMB = $memory
        WebView2WorkingSetMB = [Math]::Round($webview)
        LastLines = ($tail -join ' | ')
    } | Format-List
}
finally {
    $process.CloseMainWindow() | Out-Null
    if (-not $process.WaitForExit(8000)) { $process.Kill() }
}
