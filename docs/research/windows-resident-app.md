# Windows resident-app integration model

Research for [issue #4](https://github.com/Allann/Intercom/issues/4), based only on Microsoft platform documentation. The target is a Windows 11 x64, C#/WinUI 3 intercom that remains available while the user is signed in, uses no service, and requires no administrator setup.

## Recommendation

Build a **packaged, medium-integrity WinUI 3 desktop app using the stable Windows App SDK**, with one long-running process per signed-in Windows user. The app starts at sign-in, owns a Win32 notification-area icon, keeps its networking/audio engine alive when its window is hidden, and uses local Windows App SDK notifications for attention cards. Make the process explicitly single-instanced and redirect startup/notification activations into the resident instance.

This is a conventional desktop-resident process, not a Windows service or a suspended UWP-style app. Closing the main window should hide it; quitting must be a separate, explicit tray command. Availability ends at sign-out or process exit.

MSIX package identity is the right default because packaging gives a predictable install/update model and unlocks Windows extensibility points. WinUI 3 templates are packaged by default. A packaged WinUI 3 desktop app normally runs full-trust at medium integrity rather than in AppContainer, so these Win32 integrations do not require elevation. [Package and deploy overview](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/) · [Capability declarations](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations)

## Integration choices

### Launch at sign-in

Declare `desktop:Extension Category="windows.startupTask"` with a stable `TaskId`, the app executable, `EntryPoint="Windows.FullTrustApplication"`, and a user-facing display name. Use `Windows.ApplicationModel.StartupTask` to expose the state in app settings.

The user remains in control: startup tasks appear in Task Manager/Windows startup settings; if the user disables one, the app cannot silently re-enable it. The MVP should therefore show a clear disabled/by-policy status and a link or instruction to Windows Startup Apps settings. Avoid the restricted `ImmediateRegistration` capability and do not use registry `Run` keys or a scheduled task. [Desktop startup extension](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/desktop-to-uwp-extensions#start-an-executable-file-when-users-log-into-windows) · [`desktop:StartupTask` schema](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-desktop-startuptask) · [`StartupTaskState`](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.startuptaskstate)

Practical product choice: enable startup as part of first-run setup with an explicit explanation, then reflect the actual OS state. Do not promise that the app is reachable before sign-in.

### System-tray residency

WinUI 3 has no first-party high-level tray control. Use the supported Win32 `Shell_NotifyIcon` API through interop, associating a stable GUID with the WinUI window's HWND (or a dedicated hidden message window). Add using `NIM_ADD`, immediately select `NOTIFYICON_VERSION_4` using `NIM_SETVERSION`, update status using `NIM_MODIFY`, and remove using `NIM_DELETE` on orderly exit. Handle mouse and keyboard activation and return focus with `NIM_SETFOCUS` after dismissing a tray menu. [Shell_NotifyIcon](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shell_notifyiconw) · [Taskbar notification-area integration](https://learn.microsoft.com/en-us/windows/win32/shell/taskbar#adding-modifying-and-deleting-icons-in-the-notification-area)

Windows may place the icon in the overflow area; the app cannot require or programmatically force a permanently visible tray position. Use one icon, with status changes conveyed accessibly in its tooltip/menu as well as visually. [Notification-area guidance](https://learn.microsoft.com/en-us/windows/win32/uxguide/winenv-notification)

### Process lifecycle and activation

WinUI 3 apps are multi-instanced by default. Register one constant key at the earliest point in a custom `Main` using `Microsoft.Windows.AppLifecycle.AppInstance.FindOrRegisterForKey`. If another process already owns it, redirect activation with `RedirectActivationToAsync` and exit before creating XAML UI. The resident instance handles ordinary launch and notification activation without duplicating discovery sockets, hotkeys, or tray icons. [App instancing](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/applifecycle/applifecycle-instancing) · [Single-instance C# implementation](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/applifecycle/applifecycle-single-instance)

Do not design LAN reachability around a background task. The resident medium-integrity desktop process continues running with its window hidden and owns the live peer connections. There is no service, pre-login execution, or guarantee after the user selects Quit or Windows terminates the process.

### Global quick-chat shortcut

`RegisterHotKey` is the supported lightweight mechanism for a system-wide shortcut and posts `WM_HOTKEY` to the owning window/thread. Registration can fail when another application owns the combination; Windows-key combinations are reserved for the OS, and F12 is reserved for debugging. The settings UI must validate registration, report conflicts, retain the previous working binding, and call `UnregisterHotKey` on change/exit. [`RegisterHotKey`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey)

However, `WM_HOTKEY` is an invocation event, not a documented press-and-release stream. It is sufficient for **toggle-to-talk** or **open Quick Chat**, but does not by itself implement “hold to talk, release to stop.” A low-level keyboard hook can observe `WM_KEYDOWN` and `WM_KEYUP`, but Microsoft warns that slow callbacks can be silently removed and recommends raw input in many monitoring cases. A global hook also has a substantially larger privacy/maintenance footprint than `RegisterHotKey`. [`LowLevelKeyboardProc`](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc)

Therefore:

1. Ship a configurable `RegisterHotKey` action for toggle/open behavior.
2. Resolve hold-to-talk through a small prototype comparing (a) `RegisterHotKey` followed by narrowly scoped key-state/release detection, (b) raw input, and (c) a dedicated `WH_KEYBOARD_LL` hook thread.
3. The chosen implementation must never suppress keystrokes, must ignore injected events where appropriate, do no work in a hook callback beyond queueing, and must prove release handling, conflict behavior, lock-screen/session changes, accessibility, and recovery.

### Local app notifications and acknowledgements

Use `Microsoft.Windows.AppNotifications.AppNotificationManager` for local notifications; do not use WNS/push because the receiver's resident process already receives LAN messages and the product forbids cloud infrastructure. Windows App SDK local notifications support text, inline/hero images, custom audio, and action buttons. This fits the four attention presets and their three acknowledgement actions. [App notifications overview](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/) · [Notification content and actions](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-content)

For WinUI/Windows App SDK, an action activates the app; it can inspect activation arguments and handle the acknowledgement without showing the main window, then remain resident. App notifications are not supported for elevated apps, reinforcing the medium-integrity/no-admin choice. [Notification quickstart](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/notifications/app-notifications/app-notifications-quickstart?tabs=cs)

The proposed 320×320 illustration is an **asset source size**, not a guaranteed rendered square. Windows owns toast layout and may display it as an inline or hero image. Prototype each card in Windows 11 at common DPI/text scales. If a guaranteed large comic-style 320×320 popup is essential, that requires a custom app window and should be treated separately from the durable native notification/Action Center experience.

App-level Do Not Disturb should suppress creation of chime/toast notifications and queue a silent visual inbox item. Windows Focus Sessions may independently suppress notifications, so delivery acknowledgment means the resident app accepted the LAN message, not that Windows visibly presented it. [Notification UX guidance](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-ux-guidance)

### Microphone access

Declare the `microphone` device capability in `Package.appxmanifest`, even though the packaged medium-integrity desktop app is less restricted than an AppContainer app. This accurately discloses the privacy-sensitive feature and gives the package identity an individual Windows privacy toggle. Detect denial/unavailability and route to chat rather than repeatedly prompting. [Capability declarations](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations)

The app must also cope with the user disabling desktop-app microphone access at the OS level, removing the device, or another audio failure. Microphone permission is not a reason to elevate.

### Local text-to-speech

Use `Windows.Media.SpeechSynthesis.SpeechSynthesizer` and a locally installed `VoiceInformation`; it produces a `SpeechSynthesisStream` from text/SSML using an installed synthesis engine. This supports the agreed type-on-one-side/hear-on-the-other accessibility path without sending text to a cloud speech service. Enumerate installed voices, store the selected voice locally, and provide an audible preview and stop control. [Speech synthesis namespace](https://learn.microsoft.com/en-us/uwp/api/windows.media.speechsynthesis)

## Acceptance checks for implementation tickets

- Fresh MSIX install and first-run setup work without elevation.
- Startup state is accurately shown for enabled, disabled-by-user, and disabled-by-policy cases.
- At next sign-in, one process starts hidden and one tray icon appears; manually launching again activates that same process.
- Closing the window retains LAN reachability; tray Quit removes the icon and ends reachability.
- Tray icon/menu work by mouse, keyboard, high DPI, and screen reader; status is not color-only.
- A conflicting global shortcut is rejected clearly and never silently disables Quick Chat.
- Native attention notifications show the intended image, chime, and three actions; action activation sends exactly one acknowledgement without forcing the main window open.
- App DND produces no app chime/toast while retaining the queued item.
- Microphone denial/device loss falls back to text.
- TTS works with an installed Windows voice while the network is disconnected.

## Decisions and follow-up work surfaced

1. **Prototype global hold-to-talk input.** Choose the least invasive reliable release-detection mechanism; `RegisterHotKey` alone is insufficient.
2. **Prototype attention-card rendering.** Decide whether native Windows notifications are visually sufficient or whether a separate custom popup is justified; keep native actions for durable acknowledgements.
3. **Define MSIX distribution and signing.** Packaging is decided, but sideloading versus Store delivery, certificate trust, and updates are a separate deployment decision.
4. **Define unexpected-process-exit recovery.** Startup-at-sign-in does not restart a crashed process during the same session; decide whether MVP accepts this or needs a user-visible health/relaunch strategy (without a service).

