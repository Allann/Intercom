# Installing Intercom

Intercom is a private Windows app for communicating with another PC on the
same local network. This is a test build, so Windows needs one extra setup
step before it can be installed.

## Requirements

- A 64-bit PC running Windows 10 version 2004 or newer, or Windows 11.
- Administrator access for the one-time certificate step.
- Both Intercom PCs connected to the same local network.
- The Windows network profile set to **Private**, not **Public**.

## Install

1. Extract the entire zip file. Do not run the installer from inside the zip.
2. Open the extracted folder.
3. Right-click the Start button and select **Terminal (Admin)** or
   **Windows PowerShell (Admin)**.
4. Approve the Windows administrator prompt.
5. In the administrator window, change to the extracted folder and run:

   ```powershell
   $certificate = Get-ChildItem -File -Filter 'Intercom.App_*.cer' |
       Select-Object -First 1
   Import-Certificate `
       -FilePath $certificate.FullName `
       -CertStoreLocation Cert:\LocalMachine\TrustedPeople
   ```

6. Close the administrator window.
7. Open a normal, non-administrator PowerShell window in the extracted folder.
   A convenient way is to right-click an empty area in the folder and select
   **Open in Terminal**.
8. Run:

   ```powershell
   powershell.exe -ExecutionPolicy Bypass `
       -File .\Install.ps1 `
       -SkipLoggingTelemetry
   ```

9. When installation finishes, open **Intercom** from the Start menu.

Keep the whole extracted folder until installation is complete. It contains
the Windows components the installer may need in addition to Intercom itself.

## First use

1. Allow microphone access when Windows asks.
2. If Windows Firewall asks, allow Intercom on **Private networks** only.
3. Start Intercom on the other PC and wait for it to appear.
4. Select **Pair** on one PC.
5. Check that the six-digit verification code is identical on both PCs.
6. Accept the pairing on both PCs and give the other PC a recognizable name.
7. Try a text message first, followed by hold-to-talk or toggle-on/off audio.

Closing the main window leaves Intercom running in the notification area. Use
the Intercom tray icon when you want to reopen or quit it.

## If the other PC does not appear

- Confirm both PCs are connected to the same router or local network.
- In Windows **Settings > Network & internet**, confirm the active network is
  set to **Private**.
- Restart Intercom on both PCs after changing the network profile.
- Guest Wi-Fi and some mesh/router settings isolate devices from one another.
  Move both PCs to the main Wi-Fi network or disable client/AP isolation.
- Check that a security product has not blocked Intercom's local-network
  traffic.

## Uninstall

Open **Settings > Apps > Installed apps**, find **Intercom**, open its menu,
and select **Uninstall**.

The trusted test certificate can also be removed afterward if Intercom will no
longer be installed. Open PowerShell as Administrator and run:

```powershell
Get-ChildItem Cert:\LocalMachine\TrustedPeople |
    Where-Object Subject -eq 'CN=Intercom Development' |
    Remove-Item
```
