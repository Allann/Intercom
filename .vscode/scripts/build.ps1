<#
.SYNOPSIS
  Builds Intercom.App with Visual Studio's MSBuild. Plain `dotnet build` does
  not work for this project — see src/Intercom.App/BUILD.md for why.
#>
param(
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$vswhere = "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) {
    throw "vswhere.exe not found at '$vswhere'. Install Visual Studio, or update this script's path."
}

$vsInstallPath = & $vswhere -latest -prerelease -products * -requires Microsoft.Component.MSBuild -property installationPath
if (-not $vsInstallPath) {
    throw "vswhere found no Visual Studio installation with MSBuild."
}

$msbuild = Join-Path $vsInstallPath "MSBuild\Current\Bin\MSBuild.exe"
if (-not (Test-Path $msbuild)) {
    throw "MSBuild.exe not found at expected path '$msbuild'."
}

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$csproj = Join-Path $repoRoot "src\Intercom.App\Intercom.App.csproj"

Write-Host "Building with $msbuild ..."
& $msbuild $csproj -restore -p:Configuration=$Configuration -p:Platform=x64
if ($LASTEXITCODE -ne 0) {
    throw "Build failed (exit code $LASTEXITCODE)."
}
