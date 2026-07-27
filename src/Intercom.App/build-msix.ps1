[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [string] $Publisher = 'CN=Intercom Development'
)

$ErrorActionPreference = 'Stop'
$projectDirectory = $PSScriptRoot
$repositoryRoot = Split-Path (Split-Path $projectDirectory -Parent) -Parent
$artifactsDirectory = Join-Path $repositoryRoot 'artifacts\msix'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'

if (-not (Test-Path -LiteralPath $vswhere)) {
    throw 'Visual Studio Installer (vswhere.exe) was not found.'
}

$msbuild = & $vswhere -latest -prerelease -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) {
    throw 'Visual Studio MSBuild was not found. Install the Windows application development workload.'
}

New-Item -ItemType Directory -Path $artifactsDirectory -Force | Out-Null

$friendlyName = 'Intercom MSIX Development'
$certificate = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $Publisher -and $_.FriendlyName -eq $friendlyName -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if (-not $certificate) {
    $certificate = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $Publisher `
        -FriendlyName $friendlyName `
        -CertStoreLocation Cert:\CurrentUser\My `
        -KeyUsage DigitalSignature `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') `
        -KeyAlgorithm RSA `
        -KeyLength 3072 `
        -HashAlgorithm SHA256 `
        -NotAfter (Get-Date).AddYears(5)
}

$publicCertificate = Join-Path $artifactsDirectory 'Intercom-Development.cer'
Export-Certificate -Cert $certificate -FilePath $publicCertificate -Force | Out-Null

& $msbuild (Join-Path $projectDirectory 'Intercom.App.csproj') `
    -restore `
    -p:Configuration=$Configuration `
    -p:Platform=x64 `
    -p:GenerateAppxPackageOnBuild=true `
    -p:AppxSymbolPackageEnabled=false `
    -p:AppxPackageSigningEnabled=true `
    -p:PackageCertificateThumbprint=$($certificate.Thumbprint) `
    -p:AppxPackageDir="$artifactsDirectory\"

if ($LASTEXITCODE -ne 0) {
    throw "MSIX build failed with exit code $LASTEXITCODE."
}

Write-Host "Signed MSIX output: $artifactsDirectory"
Write-Host "Signing certificate: $($certificate.Thumbprint)"
Write-Host "Public certificate: $publicCertificate"
Write-Warning 'Installing a self-signed MSIX requires trusting this certificate in Cert:\LocalMachine\TrustedPeople from an elevated PowerShell session. Use a CA/enterprise-signed certificate to avoid that machine setup.'
