# Building Intercom.App

## Toolchain gotcha

Plain `dotnet build` / `dotnet run` **fails** on this project with:

```
error MSB4062: The "Microsoft.Build.Packaging.Pri.Tasks.ExpandPriContent" task
could not be loaded... Microsoft.Build.Packaging.Pri.Tasks.dll ... The system
cannot find the file specified.
```

The .NET SDK's own bundled MSBuild doesn't ship the MSIX/PRI packaging task
assemblies that `Microsoft.WindowsAppSDK`'s build targets need (even for an
unpackaged build — PRI resource generation runs regardless). Those assemblies
come from Visual Studio's install instead. Build with VS's MSBuild directly:

```
"C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\MSBuild.exe" Intercom.App.csproj -restore -p:Configuration=Debug -p:Platform=x64
```

(Adjust the path to whatever your actual Visual Studio install path is —
`vswhere.exe` or the Visual Studio Installer can confirm it. `dotnet new list`
also confirmed this machine has no WinUI 3 project templates installed via
`dotnet new`; they come from the Visual Studio "Windows application
development" workload, not a `dotnet workload install` package.)

## Current status (issue #18)

Builds clean (0 warnings, 0 errors) as an **unpackaged** app
(`WindowsPackageType=None`, `WindowsAppSDKSelfContained=true`). Implemented so far:

- Custom `Main` (`Program.cs`) with single-instance handling via
  `AppInstance.FindOrRegisterForKey` — a second launch redirects activation to
  the existing instance instead of opening a second window.
- Tray icon (`Tray/TrayIcon.cs`, `Shell_NotifyIcon` interop) and a subclassed
  window procedure (`Tray/TrayMessagePump.cs`) handling left-click (show
  window) and right-click (Open/Quit context menu via `TrackPopupMenuEx`).
- Closing the main window hides it instead of exiting
  (`AppWindow.Closing` cancelled + `Hide()`); only the tray Quit action calls
  `MainWindow.Quit()`, which tears down the tray icon/pump and exits.
- Crash visibility (`Diagnostics/CrashMarker.cs`): a marker file written at
  startup and removed on clean shutdown; if present at the next startup, the
  previous run didn't exit cleanly.
- Startup-task scaffolding (`Startup/StartupTaskService.cs`) — **this will
  throw at runtime until MSIX packaging is wired up**, since
  `Windows.ApplicationModel.StartupTask` requires package identity. The
  exception is caught in `App.xaml.cs` rather than crashing the app.

## Not yet done

- **MSIX packaging and signing** (ADR-0003): self-signed cert generation,
  `Cert:\CurrentUser\TrustedPeople` trust step, `WindowsPackageType=MSIX`,
  actual sideloaded install. This is required for `StartupTask` to actually
  work and for the "fresh MSIX install without elevation" acceptance
  criterion. Not attempted yet in this pass.
- **Real interactive verification.** This was built and compiled from an
  automated shell with no ability to click the tray icon, trigger a second
  launch, or confirm the context menu/keyboard accessibility actually behave
  as intended. Run it yourself and confirm:
  1. Launching a second instance doesn't open a second window.
  2. Closing the window hides it; the tray icon remains; left-click reopens
     the window; right-click shows Open/Quit; Quit actually exits.
  3. Keyboard/screen-reader access to the tray context menu.
  4. A forced `taskkill` followed by relaunch surfaces the "closed
     unexpectedly" debug trace (visible today only in the debugger output —
     wiring it into the shell UI is follow-up work once that UI exists).
- Tray icon is currently the stock `IDI_APPLICATION` system icon — a real
  `.ico` asset in the comic-intercom visual language (issue #8) replaces this
  later.
