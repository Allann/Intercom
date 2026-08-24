# QA procedure: Complex production behavior coverage

## Preparation

1. Use a clean test machine with the supported .NET SDK and the Windows App SDK requirements for Intercom.
2. Keep the current attention-card notification changes and their acceptance artifacts in the verification scope.
3. Keep generated files out of the human-authored production-code scope.
4. Set the OpenCover CRAP threshold to 15.

## Deterministic Intercom.Core coverage gate

1. Build the production projects and all test projects.
2. Run the complete deterministic test suite and collect one OpenCover report.
3. Confirm that all tests pass.
4. Confirm that the report includes all human-authored Intercom.Core production code.
5. Confirm that generated code does not contribute methods or failures to the gate.
6. Evaluate every human-authored Intercom.Core method against the OpenCover CRAP threshold of 15.
7. Confirm that no method is above the threshold.
8. Run the same suite again without a product change.
9. Confirm that the second run passes and gives the same threshold result.
10. If a method is above the threshold, confirm that the gate fails and identifies the method. Do not accept a report that hides the method by treating human-authored code as generated code.

## Resident-app executable seams

1. List each complex resident-app behavior that depends on Windows, WinUI, or device hardware.
2. For each behavior, identify the production decision and the requested native effect.
3. Run its automated verification with a controlled replacement for the native effect.
4. Confirm that the production decision executes at runtime.
5. Confirm that the controlled replacement records the correct request, including important values and order.
6. Confirm that error, denied-permission, unavailable-device, and cancellation decisions run where they apply.
7. Confirm that no test uses production source text as evidence for runtime behavior.
8. Confirm that each effect which cannot be observed deterministically appears in the Windows and hardware checks below.

## Windows and hardware checks

1. On supported Windows 10 and Windows 11 test devices, start the resident app and confirm that it remains available from the Windows notification area after its main window closes.
2. Use the configured push-to-talk control. Confirm that press and release are detected while another app has focus, and confirm that the control does not remain active after release.
3. Select an available microphone and speaker. Start and stop voice communication with an approved peer. Confirm that capture and playback use the selected devices and stop cleanly.
4. Remove or disable an active audio device during voice communication. Confirm that Intercom reports the unavailable device and remains responsive.
5. Enable and disable launch-at-sign-in in Intercom settings. Confirm that Intercom shows the actual Windows state, including a state that Windows policy prevents the app from changing.
6. Close and reopen the main window from the Windows notification area. Then use the explicit quit action and confirm that the resident app exits.
7. Leave the device idle past the configured idle threshold, then provide user input. Confirm that the peer availability changes after the required idle samples and returns immediately after input.
8. Open chat content that uses spoken chat on a device with an installed Windows speech voice. Confirm that formatting syntax is omitted and that shared links and a shared image are announced as specified.
9. Run the existing attention-card notification presentation QA procedure on Windows 10 and Windows 11. Confirm that the attention card remains available for acknowledgement when Windows suppresses its popup.
10. For each check, record the Windows version, relevant device or policy state, expected visible result, actual result, and diagnostic evidence.

## Final review

1. Confirm that the deterministic coverage gate passes with no human-authored Intercom.Core method above 15.
2. Confirm that every complex resident-app production decision has runtime test evidence.
3. Confirm that every native effect without deterministic observation has a completed Windows or hardware QA record.
4. Confirm that the attention-card notification feature and QA procedure are present and included in the review.
5. Confirm that unrelated changes to `AGENTS.md` and `.claude` are unchanged.
