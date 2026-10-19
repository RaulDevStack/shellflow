<#
.SYNOPSIS
  Creates the public GitHub repository "shellflow", pushes the code and publishes the release with its assets.
.DESCRIPTION
  Requires the GitHub CLI, signed in once:  winget install GitHub.cli ; gh auth login
  Run build\release.ps1 first so artifacts\release contains the installer, the portable zip and SHA256SUMS.txt.
  The script never overwrites an existing repository or release.
#>
param(
    [string]$Name = 'shellflow',
    [switch]$Private
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $root

$description = 'A modern, open-source terminal for Windows: real shells via ConPTY, xterm.js rendering, nested split panes, a command palette, a cd folder picker, Explorer integration and Hyprland-style animations. Built with WinUI 3 and .NET.'
$topics = @(
    'terminal', 'terminal-emulator', 'windows', 'windows-11', 'winui3', 'windows-app-sdk', 'dotnet', 'csharp',
    'xtermjs', 'webview2', 'conpty', 'powershell', 'split-panes', 'tiling', 'developer-tools', 'command-palette',
    'cli', 'shell', 'hyprland', 'msi-installer')

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'GitHub CLI not found. Install it with: winget install GitHub.cli' }
gh auth status | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Not signed in to GitHub. Run: gh auth login' }

$version = ([xml](Get-Content 'Directory.Build.props')).Project.PropertyGroup.Version | Select-Object -First 1
$tag = "v$version"
$assets = Get-ChildItem 'artifacts\release' -File -ErrorAction SilentlyContinue
if (-not $assets -or -not ($assets.Name -match '\.msi$')) { throw 'Release assets missing. Run build\release.ps1 first.' }

$owner = gh api user --jq .login
$repo = "$owner/$Name"
Write-Host "Publishing ShellFlow $version to github.com/$repo" -ForegroundColor Cyan

# "Not found" is the expected answer; gh reports it on stderr, which must not abort the script.
$ErrorActionPreference = 'Continue'
gh repo view $repo *> $null
$exists = $LASTEXITCODE -eq 0
$ErrorActionPreference = 'Stop'
if ($exists) { throw "Repository $repo already exists; refusing to overwrite it." }

# Links in the docs point at RaulDevStack/shellflow; adjust them if the account or name differs.
if ($repo -ne 'RaulDevStack/shellflow') {
    $files = Get-ChildItem -Recurse -Include *.md, *.yml -Path . | Where-Object { $_.FullName -notmatch '\\(bin|obj|artifacts|node_modules)\\' }
    foreach ($file in $files) {
        $text = [IO.File]::ReadAllText($file.FullName)
        if ($text.Contains('RaulDevStack/shellflow')) {
            [IO.File]::WriteAllText($file.FullName, $text.Replace('RaulDevStack/shellflow', $repo), (New-Object Text.UTF8Encoding($false)))
        }
    }
    git add -A
    git commit -m "Point documentation links at $repo"
}

$visibility = if ($Private) { '--private' } else { '--public' }
gh repo create $Name $visibility --description $description --source . --remote origin --push
if ($LASTEXITCODE -ne 0) { throw 'Creating the repository failed.' }

gh repo edit $repo --add-topic ($topics -join ',') --enable-issues --enable-wiki=false
# Lets people report vulnerabilities privately (linked from docs/SECURITY.md).
gh api -X PUT "repos/$repo/private-vulnerability-reporting" --silent

$prerelease = if ($version -like '0.*') { @('--prerelease') } else { @() }
gh release create $tag $assets.FullName --title "ShellFlow $version" --notes-file "docs/release-notes/$tag.md" @prerelease
if ($LASTEXITCODE -ne 0) { throw 'Creating the release failed.' }

Write-Host "`nDone: https://github.com/$repo" -ForegroundColor Green
Write-Host "Release: https://github.com/$repo/releases/tag/$tag"
