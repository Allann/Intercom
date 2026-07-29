---
name: run-intercom-app
description: Build, run, and drive Intercom.App (the WinUI 3 desktop app). Use when asked to build, run, start, launch, or screenshot Intercom, or to confirm a change works in the real running app rather than just its tests.
---

Intercom.App is a packaged (MSIX-identity) WinUI 3 desktop app. It cannot be
driven with `chromium-cli` — it's a native Win32/WinRT window. Drive it with
the Windows UI Automation script at
`.claude/skills/run-intercom-app/driver.ps1`, run under `pwsh`. All paths
below are relative to `src/Intercom.App/`.

This only works on Windows with Visual Studio (or Build Tools) installed —
there is no headless/Linux path for a WinUI 3 app.

## Prerequisites

- Visual Studio 2022+ with the "Windows application development" workload,
  specifically one that includes `Microsoft.Build.Packaging.Pri.Tasks.dll`
  (WinUI resource/PRI packaging). Plain `dotnet build` cannot build this
  project — the .NET SDK's MSBuild lacks that task.
- .NET SDK 10.0 (pinned by the repo's `global.json`).
- PowerShell 7+ (`pwsh`) — the driver uses `System.Windows.Automation`
  (UIAutomationClient/UIAutomationTypes), which loads fine under `pwsh` even
  though it's a .NET Framework-era WPF assembly.

Find an MSBuild that actually has the Pri packaging task — not every VS
install does:

```powershell
Get-ChildItem "C:\Program Files\Microsoft Visual Studio","C:\Program Files (x86)\Microsoft Visual Studio" `
  -Recurse -Filter "Microsoft.Build.Packaging.Pri.Tasks.dll" -ErrorAction SilentlyContinue
```

Use the `MSBuild\Current\Bin\MSBuild.exe` under whichever VS install that
path is nested in.

## Build

```powershell
& '<path from Prerequisites>\MSBuild\Current\Bin\MSBuild.exe' `
    src\Intercom.App\Intercom.App.csproj -restore -p:Configuration=Debug -p:Platform=x64
```

Verified output: `Intercom.App -> ...\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\Intercom.App.dll`.

Building alone does **not** give you a runnable app — see the next section.

## Run (agent path)

The built `Intercom.App.exe` cannot be launched directly
(`.\Intercom.App.exe`) — WinUI 3's `AppInstance`/deployment APIs need MSIX
package identity, and running the loose exe throws
`COMException 0x80040154 (Class not registered)` inside
`DeploymentManagerCS`. If the app has ever been deployed from Visual Studio
(F5, or a prior `Add-AppxPackage -Register` against this same
`Intercom.App.csproj` output folder) it's already registered as an
**AppX dev-mode package** pointing straight at your build output — check:

```powershell
Get-AppxPackage -Name "Allann.Intercom" | Select-Object PackageFamilyName, InstallLocation, IsDevelopmentMode
```

If `InstallLocation` matches the `bin\x64\<Config>\...\win-x64` folder you
just built into, you're set — no separate install/package step needed after
a rebuild. If no such package exists yet, register the manifest once from
Visual Studio (Deploy on the `Intercom.App` project) or via
`Add-AppxPackage -Register .\Package.appxmanifest` from the build output
folder.

Then drive it with `driver.ps1`:

```powershell
cd src\Intercom.App\.claude\skills\run-intercom-app
pwsh .\driver.ps1 launch                          # start (or attach), prints PID
pwsh .\driver.ps1 tree                             # dump the UIA control tree
pwsh .\driver.ps1 screenshot C:\path\to\out.png    # screenshot just the app window
pwsh .\driver.ps1 click "Simulate peer reply"      # invoke a button by UIA Name
pwsh .\driver.ps1 type "Type a message..." "hi"    # set an edit control's value
pwsh .\driver.ps1 click "id:ChatSendButton"        # ...or by AutomationId (prefix id:)
pwsh .\driver.ps1 close                            # close the main window
```

`launch` resolves the app via its AppUserModelID
(`Allann.Intercom_azfvp4fdjgk16!App`) through `shell:AppsFolder`, which is
what actually gives the process package identity — this is the equivalent
of double-clicking the Start Menu tile, scripted.

Verified working flow (this session): `launch` → `tree` (confirmed
`ChatInputBox`/`ChatSendButton`/`SimulatePeerReplyButton`/
`SimulatePeerAttentionCardButton` AutomationIds) → `type` a chat message →
`click Send` → `click "Simulate peer reply"` → `screenshot` showed both
bubbles rendered ("delivered" / "received") → `click "Simulate peer sends a
card"` → an attention card appeared with a live `Ack` button → `click Ack`
acknowledged it → `close`.

## Run (human path)

Launch normally via the Start Menu ("Intercom") or
`explorer.exe "shell:AppsFolder\Allann.Intercom_azfvp4fdjgk16!App"`. Useless
for unattended verification since nothing captures its output — use the
driver instead.

## Test

```powershell
dotnet test tests\Intercom.App.Tests\Intercom.App.Tests.csproj -c Debug
```

Plain `dotnet test` works fine here (unlike building the app itself) because
the test project doesn't need WinUI PRI packaging. Verified: 484 passed, 0
failed.

## Gotchas

- **Loose exe won't launch.** `Intercom.App.exe` run directly throws
  `COMException 0x80040154` from `DeploymentManagerCS` — it has no package
  identity. Always launch via `shell:AppsFolder\<AUMID>` (see Run section).
- **Closing the window doesn't quit the process.** Intercom is a
  resident/tray app (see `BUILD.md`'s manual acceptance checks) — `driver.ps1
  close` closes the main window but the process and tray icon persist by
  design. Use `Stop-Process -Name Intercom.App -Force` if you need the
  process gone (e.g. before rebuilding, since the exe/dll will be locked
  while running).
- **`click "Ack"` can fail if no card is pending.** The attention-card Ack
  button is only present per-card and shares the Name `"Ack"` across
  instances with no stable AutomationId (it's generated per-card in a data
  template) — `click "Ack"` grabs whichever one UIA finds first. If the demo
  card was already acknowledged in a previous run (state persists on disk
  between launches), there may be no `Ack` button at all and the call throws
  "No control matching 'Ack' found." Send a fresh card first
  (`click "Simulate peer sends a card"`) if you need one to Ack.
- **Two controls are both named `"Send"`** (chat send vs. attention-card
  send) — `click "Send"` matches the first one UIA encounters (the chat
  send button). Use `click "id:SendAttentionCardButton"` to target the
  attention-card one unambiguously.
- **Not every VS install has the WinUI packaging task.** This machine has
  multiple VS instances; only one (an Insiders build under
  `C:\Program Files\Microsoft Visual Studio\18\Insiders`) had
  `Microsoft.Build.Packaging.Pri.Tasks.dll`. The BuildTools-only 2022
  instance did not, and building with its MSBuild would fail resource
  generation. Always locate the DLL first rather than assuming `vswhere
  -latest` points at a working one.

## Troubleshooting

- **`COMException (0x80040154): Class not registered` at
  `DeploymentManagerCS..cctor` on launch**: you ran the loose `.exe`
  instead of launching through its AppX identity. Use
  `driver.ps1 launch` (or `shell:AppsFolder\<AUMID>` manually).
- **Build succeeds but no `Get-AppxPackage` entry exists**: the manifest was
  never registered on this machine. Deploy once from Visual Studio, or run
  `Add-AppxPackage -Register .\Package.appxmanifest` from inside the build
  output directory.
