<#
.SYNOPSIS
  Registers the loose Debug build output as a development-mode package (no
  signing/cert/elevation — requires Developer Mode, already the case for
  this workflow) and launches it WITH package identity via
  Invoke-CommandInDesktopPackage, so Windows.ApplicationModel.StartupTask
  and other packaged-identity APIs actually resolve instead of throwing
  REGDB_E_CLASSNOTREG.

  This is the piece Visual Studio's F5 does invisibly for MSIX projects;
  VS Code has no equivalent, so this script + an "attach" launch config
  (processName-based, no manual PID needed) approximates it.
#>
param(
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

# A previous run may still be resident (ADR-0003: closing hides to tray rather
# than exiting) — kill it first so this debug session always attaches to a
# freshly built binary, not stale in-memory code.
Get-Process -Name "Intercom.App" -ErrorAction SilentlyContinue | Stop-Process -Force

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$binRoot = Join-Path $repoRoot "src\Intercom.App\bin\x64\$Configuration"

$manifest = Get-ChildItem -Path $binRoot -Filter "AppxManifest.xml" -Recurse |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $manifest) {
    throw "No AppxManifest.xml found under '$binRoot'. Build first."
}

# Dev-mode registration blocks re-registering the same version from the same
# path ("already installed, reinstallation blocked") even though the binary
# underneath has changed — remove any prior registration first so this always
# picks up a fresh build rather than erroring or silently running stale code.
Get-AppxPackage -Name "*Intercom*" | Remove-AppxPackage -ErrorAction SilentlyContinue

Write-Host "Registering $($manifest.FullName) ..."
Add-AppxPackage -Register $manifest.FullName

$pkg = Get-AppxPackage -Name "*Intercom*" | Select-Object -First 1
if (-not $pkg) {
    throw "Package registration succeeded but Get-AppxPackage found nothing matching '*Intercom*'."
}

$manifestXml = Get-AppxPackageManifest $pkg
$appId = $manifestXml.Package.Applications.Application.Id

$aumid = "$($pkg.PackageFamilyName)!$appId"
Write-Host "Launching $aumid with package identity ..."

# Invoke-CommandInDesktopPackage waits synchronously for the launched process
# to exit (even backgrounded in a job it still hangs, likely an STA/window-
# station quirk in ApplicationActivationManager) — hopeless for a resident
# tray app that runs until Quit. shell:AppsFolder is the same activation path
# Start Menu tiles use, and Start-Process returns as soon as explorer.exe has
# handed off the activation, not when the app itself exits.
Start-Process "shell:AppsFolder\$aumid"

Start-Sleep -Seconds 5
$expectedExecutable = Join-Path $manifest.DirectoryName "Intercom.App.exe"
$processes = @(Get-Process -Name "Intercom.App" -ErrorAction SilentlyContinue)
$proc = $processes |
    Where-Object { $_.Path -eq $expectedExecutable } |
    Sort-Object StartTime -Descending |
    Select-Object -First 1
if (-not $proc -or $proc.HasExited) {
    $recentError = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = (Get-Date).AddMinutes(-1) } -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -match 'Intercom.App' } |
        Select-Object -First 1
    $detail = if ($recentError) { "`n$($recentError.Message)" } else { '' }
    throw "Packaged activation did not leave a healthy Intercom.App process.$detail"
}
Write-Host "Running as PID $($proc.Id)."
