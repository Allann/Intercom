# QA procedure: Attention card notification presentation

## Preparation

1. Prepare one receiving peer on Windows 10 and one receiving peer on Windows 11.
2. Prepare a sending peer that is approved by each receiving peer.
3. On each receiving peer, set the interruption mode to available.
4. Confirm that Intercom notification permission is enabled in Windows.
5. Start the resident app on each receiving peer, close its main window, and confirm that it remains available from the Windows notification area.

## App notification popup

1. On Windows 10, make sure that Focus Assist is off and that Windows permits Intercom notification banners.
2. From the sending peer, send an attention card to the Windows 10 receiving peer.
3. Confirm that an Intercom app notification popup appears.
4. Open the resident app from the Windows notification area.
5. Confirm that the attention card is available for acknowledgement.
6. Open the Intercom diagnostics view.
7. Confirm that diagnostics identify the attention card and show that its app notification was submitted to Windows.
8. Repeat steps 1 through 7 with the Windows 11 receiving peer. Use Windows Do Not Disturb instead of Focus Assist.
9. Confirm that Windows 10 and Windows 11 give the same Intercom product result: a popup appears, the card remains available, and diagnostics show submission.

## Banner suppression by Focus Assist or Windows Do Not Disturb

1. On Windows 10, enable Focus Assist so that it suppresses Intercom notification banners.
2. Close the Intercom main window and confirm that the resident app remains available from the Windows notification area.
3. From the sending peer, send an attention card to the Windows 10 receiving peer.
4. Confirm that Intercom does not require a popup to appear while Windows suppresses the banner.
5. Open the resident app and confirm that the attention card is available for acknowledgement.
6. Open the Intercom diagnostics view.
7. Confirm that diagnostics identify the attention card and show that its app notification was submitted to Windows. Windows does not report whether Focus Assist or Do Not Disturb suppressed the popup after submission.
8. Acknowledge the attention card.
9. Confirm on the sending peer that the attention card is acknowledged.
10. Confirm on the receiving peer that the attention card no longer requires acknowledgement.
11. Repeat steps 1 through 10 on Windows 11 with Windows Do Not Disturb enabled.
12. Confirm that Windows 10 and Windows 11 give the same Intercom product result: the card remains available, acknowledgement works, and diagnostics show submission.

## Banner suppression by Windows notification policy

1. On Windows 10, turn off Intercom notification banners in Windows notification settings.
2. Close the Intercom main window and send an attention card from the approved sending peer.
3. Confirm that the attention card remains available for acknowledgement in the resident app.
4. Confirm that diagnostics identify the attention card and show the Windows notification setting that blocked submission.
5. Acknowledge the card and confirm that the sending peer can see the acknowledgement.
6. Restore the original Windows notification setting.
7. Repeat steps 1 through 6 on Windows 11.
8. Confirm that Windows 10 and Windows 11 give the same Intercom product result within their notification policies.
