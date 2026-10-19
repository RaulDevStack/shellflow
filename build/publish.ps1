<#
.SYNOPSIS
  Builds a self-contained ShellFlow release.
.DESCRIPTION
  By default produces a single portable executable: artifacts\ShellFlow-<version>-<runtime>\ShellFlow.exe
  (no .NET or Windows App SDK installation needed; only the WebView2 Runtime, which Windows 11 includes).
  With -Folder it produces a folder build plus a zip instead (faster startup, no self-extraction).
  With -Installer it produces the folder build and a per-user MSI installer: artifacts\installer\ShellFlow-<version>-<arch>.msi
  (Start menu shortcut, "Open ShellFlow here" in Explorer, "shellflow" in Win+R, uninstall from Settings → Apps).
.EXAMPLE
  .\build\publish.ps1
  .\build\publish.ps1 -Runtime win-arm64
  .\build\publish.ps1 -Folder
#>
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release',
    [switch]$Folder,
    [switch]$Installer,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$platform = if ($Runtime -eq 'win-arm64') { 'ARM64' } else { 'x64' }
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Select-Object -First 1
$project = Join-Path $root 'src\ShellFlow.App\ShellFlow.App.csproj'

if (-not $SkipTests) {
    Write-Host "Running tests..." -ForegroundColor Cyan
    dotnet test (Join-Path $root 'tests\ShellFlow.Tests') -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
}

$common = @('-c', $Configuration, '-r', $Runtime, "-p:Platform=$platform", '--self-contained', 'true')

if (-not $Folder -and -not $Installer) {
    $out = Join-Path $root "artifacts\single\$Runtime"
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    Write-Host "Publishing ShellFlow $version as a single executable ($Runtime)..." -ForegroundColor Cyan
    # Native WinUI libraries, the renderer assets and resources.pri are embedded and extracted on first run.
    dotnet publish $project @common `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:IncludeAllContentForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none `
        -o $out
    if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

    # The file must stay named ShellFlow.exe: the Windows App SDK finds its embedded resources by the exe name.
    $release = Join-Path $root "artifacts\ShellFlow-$version-$Runtime"
    New-Item -ItemType Directory -Force $release | Out-Null
    $exe = Join-Path $release 'ShellFlow.exe'
    Copy-Item (Join-Path $out 'ShellFlow.exe') $exe -Force
    $size = [Math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host "Created $exe ($size MB)" -ForegroundColor Green
    return
}

$out = Join-Path $root "artifacts\publish\$Runtime"
$zip = Join-Path $root "artifacts\ShellFlow-$version-$Runtime.zip"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

Write-Host "Publishing ShellFlow $version as a folder ($Runtime)..." -ForegroundColor Cyan
dotnet publish $project @common -p:PublishReadyToRun=true -o $out
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

$notices = Get-ChildItem $out -Recurse -Filter 'THIRD-PARTY-NOTICES.txt' | Select-Object -First 1
if ($notices) { Copy-Item $notices.FullName (Join-Path $out 'THIRD-PARTY-NOTICES.txt') -Force }
Copy-Item (Join-Path $root 'README.md') $out -Force
if (Test-Path (Join-Path $root 'LICENSE')) { Copy-Item (Join-Path $root 'LICENSE') $out -Force }

if ($Installer) {
    $arch = if ($Runtime -eq 'win-arm64') { 'arm64' } else { 'x64' }
    $installerOut = Join-Path $root 'artifacts\installer'
    Write-Host "Building MSI installer..." -ForegroundColor Cyan
    dotnet build (Join-Path $root 'installer\ShellFlow.Installer.wixproj') -c $Configuration `
        "-p:PublishDir=$out" "-p:ProductVersion=$version" "-p:InstallerPlatform=$arch" -o $installerOut
    if ($LASTEXITCODE -ne 0) { throw "Installer build failed." }
    $msi = Join-Path $installerOut "ShellFlow-$version-$arch.msi"
    $size = [Math]::Round((Get-Item $msi).Length / 1MB, 1)
    Write-Host "Created $msi ($size MB)" -ForegroundColor Green
    return
}

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
$size = [Math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Created $zip ($size MB)" -ForegroundColor Green
