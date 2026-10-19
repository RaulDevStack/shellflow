<#
.SYNOPSIS
  Builds every GitHub release asset into artifacts\release:
    ShellFlow-<version>-x64.msi                 per-user installer
    ShellFlow-<version>-win-x64-portable.zip    single-file ShellFlow.exe
    SHA256SUMS.txt                              checksums of the files above
  Used locally and by .github/workflows/release.yml.
#>
param([switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Select-Object -First 1
$out = Join-Path $root 'artifacts\release'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

$publish = Join-Path $PSScriptRoot 'publish.ps1'
$testArgs = @{ SkipTests = [bool]$SkipTests }

& $publish -Installer @testArgs
if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "Installer build failed." }
& $publish -SkipTests
if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "Portable build failed." }

$msi = Join-Path $root "artifacts\installer\ShellFlow-$version-x64.msi"
Copy-Item $msi $out

# The portable exe must keep the name ShellFlow.exe (the Windows App SDK finds its embedded resources by it), so ship it zipped.
$portableDir = Join-Path $root "artifacts\ShellFlow-$version-win-x64"
$zip = Join-Path $out "ShellFlow-$version-win-x64-portable.zip"
Copy-Item (Join-Path $root 'LICENSE') $portableDir -Force
Compress-Archive -Path (Join-Path $portableDir '*') -DestinationPath $zip -Force

$sums = Get-ChildItem $out -File | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object Name | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
[IO.File]::WriteAllLines((Join-Path $out 'SHA256SUMS.txt'), $sums)

Write-Host "`nRelease assets for ShellFlow ${version}:" -ForegroundColor Green
Get-ChildItem $out | ForEach-Object { Write-Host ("  {0,-45} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB)) }
