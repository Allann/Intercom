# Building and installing Intercom.App

Intercom is a packaged, x64 WinUI 3 application. The repository pins .NET SDK
9.0 in `global.json`, while the WinUI/MSIX targets run under Visual Studio's
MSBuild because they depend on Visual Studio's PRI packaging tasks.

## Prerequisites

- Visual Studio with the Windows application development workload.
- The .NET 10 LTS SDK selected by the repository's `global.json`.
- PowerShell running as the Windows user who will install the test package.

## Compile without producing an installer

Locate MSBuild through `vswhere.exe`, then run:

```powershell
& '<Visual Studio>\MSBuild\Current\Bin\MSBuild.exe' `
    src\Intercom.App\Intercom.App.csproj `
    -restore `
    -p:Configuration=Debug `
    -p:Platform=x64
```

Plain `dotnet build` is not supported for this project. The .NET SDK's MSBuild
does not contain `Microsoft.Build.Packaging.Pri.Tasks.dll`, which WinUI resource
generation uses for both compile and package builds.

## Produce a signed MSIX

From the repository root:

```powershell
& .\src\Intercom.App\build-msix.ps1 -Configuration Release
```

The script:

1. Finds Visual Studio MSBuild with `vswhere.exe`.
2. Reuses or generates a five-year `CN=Intercom Development` code-signing
   certificate in `Cert:\CurrentUser\My`.
3. Exports its public certificate under `artifacts\msix`.
4. Generates and signs the x64 package under `artifacts\msix`.

Package creation requires no elevation. The private key stays in the user's
certificate store and is never written into the repository or artifacts
directory.

Windows AppX deployment currently requires a self-signed package certificate
in the local machine's Trusted People store. Trusting it therefore requires a
one-time elevated command:

```powershell
Import-Certificate `
    -FilePath .\artifacts\msix\Intercom-Development.cer `
    -CertStoreLocation Cert:\LocalMachine\TrustedPeople
```

This conflicts with ADR-0003's current-user/no-elevation assumption. A package
signed by a trusted public, enterprise, or managed signing certificate is the
way to retain installation without administrative machine setup.

Install the generated `.msix` by double-clicking it or with:

```powershell
Add-AppxPackage .\artifacts\msix\Intercom.App_1.0.0.0_x64_Test\Intercom.App_1.0.0.0_x64.msix
```

The package declares `IntercomStartupTask`. On first run, Intercom enables it;
Windows still retains authority over user-disabled and policy-disabled states.

## Manual acceptance checks

1. Install and launch the signed MSIX without elevation.
2. Close the main window; verify the process and tray icon remain.
3. Launch Intercom again; verify the existing window opens rather than a second
   process being created.
4. Exercise tray Open and Quit with both mouse and keyboard.
5. Verify a startup-task launch remains hidden.
6. Force-terminate Intercom, launch it again, and verify the unexpected-close
   notice appears in the window.
