# Active-device presence and contact routing

Research for [issue #6](https://github.com/Allann/Intercom/issues/6). Windows platform claims cite first-party Microsoft documentation; the distributed presence/routing rules below are proposed application protocol decisions.

## Recommendation

Derive **coarse device presence locally**, advertise only that coarse result to already-approved peers, and let every sender independently choose the best device using the same deterministic rules. Do not transmit keyboard/mouse events, application names, window titles, or a continuous “last used at” audit trail.

Model two independent dimensions:

- **Availability:** `available`, `idle`, or `unavailable`.
- **Interruption preference:** `normal` or explicit app `do-not-disturb` (DND).

`offline` is not a state a device can authoritatively announce; it is inferred by peers when its leased heartbeat expires. Keeping DND separate prevents “recent keyboard input” from accidentally overriding the user's choice not to be interrupted.

## Windows signals

### Coarse input activity

Poll Win32 `GetLastInputInfo` in the resident process. It returns the time of the last input event for the **calling session**, which is the desired scope for one signed-in user. Microsoft explicitly says it is not system-wide across all sessions and warns that its tick value is not guaranteed to be monotonic. Treat it only as an idle-duration hint: calculate a bounded age against the current tick count, tolerate an anomalous/backward result, and never expose the raw value. [`GetLastInputInfo`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getlastinputinfo)

Recommended MVP thresholds, configurable later:

- `available`: session usable and input age under **2 minutes**.
- `idle`: session usable and input age at least 2 minutes.
- `unavailable`: locked, disconnected/logged off, suspending, shutting down, or the app cannot provide its resident endpoint.

The two-minute threshold is a product heuristic, not a Windows semantic. Add hysteresis (for example, require two consecutive idle samples) so boundary jitter does not repeatedly reroute. A new local input indication can promote immediately.

Do not install keyboard hooks for presence. `GetLastInputInfo` answers the idle question without observing what was typed or clicked.

### Session lock and disconnect

Register the app's message HWND using `WTSRegisterSessionNotification`; handle `WM_WTSSESSION_CHANGE` for lock/unlock, console or remote connect/disconnect, logon/logoff, and desktop-ready events. Windows sends these messages only to registered windows. A lock, logoff, or session disconnect immediately makes this instance unavailable; unlock/desktop-ready triggers a fresh input/state evaluation rather than blindly declaring it active. [`WM_WTSSESSION_CHANGE`](https://learn.microsoft.com/en-us/windows/win32/termserv/wm-wtssession-change) · [`WTSRegisterSessionNotification`](https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/nf-wtsapi32-wtsregistersessionnotification)

Remote Desktop input belongs to that same Windows session and may make the device recently active. That is acceptable for MVP: presence means “this Windows session is being used,” not proof that someone is physically beside the PC.

### Suspend and resume

Handle `WM_POWERBROADCAST`. On `PBT_APMSUSPEND`, best-effort advertise an immediately expiring/unavailable lease, then stop assuming the network send succeeded. On `PBT_APMRESUMEAUTOMATIC`, keep the device unavailable while sockets/discovery are re-established; on user-triggered resume/unlock, recompute and publish a new lease. Windows always sends automatic-resume and may additionally send user-resume; the message does not identify the exact low-power state. [`WM_POWERBROADCAST`](https://learn.microsoft.com/en-us/windows/win32/power/wm-powerbroadcast) · [Power broadcast events](https://learn.microsoft.com/en-us/windows/win32/power/wm-powerbroadcast-messages)

Heartbeat expiry, rather than the suspend message, remains authoritative because abrupt sleep, power loss, Wi-Fi loss, or process termination can prevent a final packet.

### Sign-out and shutdown

Handle `WM_QUERYENDSESSION` only to mark the endpoint unavailable and begin minimal cleanup, return `TRUE` immediately, and defer final cleanup to `WM_ENDSESSION`. Microsoft directs apps to respect the user's intent, respond immediately, and avoid doing cleanup in the query handler. Do not block shutdown to preserve presence. [`WM_QUERYENDSESSION`](https://learn.microsoft.com/en-us/windows/win32/shutdown/wm-queryendsession) · [`WM_ENDSESSION`](https://learn.microsoft.com/en-us/windows/win32/shutdown/wm-endsession)

Again, the lease timeout covers forced termination and power loss.

### DND

DND is an explicit, locally stored Intercom setting controlled from the main UI/tray. It must not be inferred from Windows inactivity, calendar state, or Windows Focus Sessions. The app advertises only the DND bit and enforces it locally: voice, hands-free requests, attention chimes/cards, and interrupt requests stay silent/queued; text still arrives silently.

Windows Focus Sessions may independently suppress Windows app notifications, so the app must not use visible-toast presentation as its presence or delivery signal. [Windows notification UX guidance](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-ux-guidance)

## Presence lease

Each running device periodically sends approved peers a small authenticated presence record:

```text
contact_id          locally configured person/contact grouping
device_id           stable paired device identity
incarnation_id      random value generated each process start
sequence            monotonically increasing within the incarnation
availability        available | idle | unavailable
dnd                 true | false
idle_age_bucket     <2m | 2-10m | >10m (omit when unavailable)
lease_seconds       advertised validity duration
capabilities        text, receive_audio, send_audio, tts, ...
```

Privacy rules:

- Send presence only over the authenticated channel to approved family peers.
- Do not broadcast contact identity, DND, activity, or capabilities in unauthenticated LAN discovery.
- Store only the latest record needed for routing; do not retain presence history.
- Bucket the idle age. Do not transmit exact Windows input ticks or exact keystroke timestamps.

Recommended cadence: heartbeat every **10 seconds**, lease valid for **30 seconds**, with an immediate update on meaningful state changes. Add randomized jitter to avoid devices synchronizing. These are MVP protocol constants to validate under normal LAN sleep/resume and Wi-Fi roaming, not Windows requirements.

The receiver accepts only a sequence newer than the last one for the same `(device_id, incarnation_id)`. A changed incarnation resets the sequence. It records its own monotonic receive time and expires the record locally after the lease; it does not trust the sender's wall clock. This avoids clock synchronization as a correctness dependency.

## Estimating the most recently active device

Exact cross-PC “last active time” cannot be safely compared without trusting synchronized wall clocks. Instead, each heartbeat carries a coarse idle-age bucket. A sender ranks live devices for a contact as follows:

1. Exclude expired or explicitly unavailable devices.
2. Exclude DND devices for interrupting actions. For silent text delivery, keep them eligible but never chime.
3. Prefer `available` over `idle`.
4. Within `available`, prefer the lowest advertised idle bucket; then prefer the most recently received state-changing activity update.
5. Break a remaining tie deterministically using stable `device_id` ordering.

The result is intentionally “best current hint,” not surveillance-grade certainty. Network latency and bucket boundaries can produce ties; deterministic tie-breaking ensures all implementations behave predictably.

## Routing by interaction

### Quick Chat and one-to-one live voice

Route to exactly one highest-ranked non-DND device with the required audio capability. Before streaming, perform a short endpoint handshake; if it fails or the lease expires, try the next ranked device once, then fall back to the contact's text conversation. Never send live audio simultaneously to two devices merely because activity is tied.

If all live devices are DND, keep the attempt silent and tell the sender the contact is in DND. If there are no eligible devices, show offline/unreachable and retain text as not delivered under the agreed no-mailbox semantics.

### Text and attention cards

When one device is clearly available, send to that device first. To support a person's two PCs without a central service:

- If delivery is acknowledged quickly, do not fan out.
- If the selected endpoint does not acknowledge within a short routing window, or all endpoints are idle/tied, send the same interaction ID to every live eligible device for that contact.
- DND devices may receive text/silent queued attention state, but must not show/chime an interrupting card.
- The first user acknowledgement wins. The original sender then broadcasts a `resolved(interaction_id)` message to the contact's other live devices so they withdraw duplicate notifications.

All receive and acknowledgement operations must be idempotent by interaction ID. The sender is only a temporary transaction coordinator, not a server: if it disappears, duplicate cards may remain until locally dismissed/expired, but no peer becomes a privileged permanent coordinator.

### “Follow me” semantics

When activity moves from one PC to another, only **new** interactions route to the newly preferred device. Do not migrate an established voice stream or silently transfer an already-open conversation. An outstanding fanned-out attention card may be withdrawn from sibling devices after one is acknowledged.

## Stale state and failure rules

- **Lease expired:** treat as offline immediately for new routing.
- **Explicit unavailable:** stop new routing immediately, but still rely on expiry if the final update was lost.
- **Process restart:** new incarnation supersedes the old one; never accept a late sequence from the old incarnation as current.
- **Network partition:** each side independently expires the other; no split-brain election is needed.
- **Tie:** deterministic stable-device-ID choice for live voice; fan-out for silent text/attention according to the rules above.
- **DND changed during voice setup:** receiver rejects setup; sender reroutes only to another non-DND device.
- **DND changed during established voice:** receiver locally mutes/ends according to the voice-session product rule; presence merely advertises the new preference.
- **System clock changes:** no effect on lease validity or ordering because local monotonic receive time and per-incarnation sequence are used.

## Acceptance checks for implementation tickets

- Keyboard/mouse use promotes only the local signed-in session; no raw input content leaves the device.
- After two minutes without input, the device becomes idle without rapid boundary flapping.
- Lock, disconnect, suspend, and shutdown make the endpoint unavailable; abrupt power/network loss becomes offline within the lease bound.
- Resume/unlock does not advertise available until the resident endpoint has reconnected and reevaluated state.
- DND always beats recent activity for voice/chime routing.
- Two active devices resolve a live-voice tie deterministically and never both play the same one-to-one stream.
- Attention fan-out deduplicates by interaction ID; acknowledging on one PC withdraws the sibling notification when reachable.
- Restarted processes cannot be overwritten by delayed packets from a prior incarnation.
- Skewing either PC's wall clock does not alter lease expiry or route ordering.
- Discovery traffic visible to an unpaired LAN device reveals no contact/activity/DND details.

## Decisions and follow-up work surfaced

1. **Confirm presence thresholds:** prototype 2-minute active threshold, 10-second heartbeat, 30-second lease, and the short text/attention acknowledgement window on the two initial household PCs.
2. **Define contact/device membership:** pairing is device-based, but multi-PC routing requires a local, authenticated mapping of multiple approved device identities to one contact; define how that mapping is created and reconciled.
3. **Define sibling-notification resolution:** specify expiry and user experience when the original sender is offline before it can relay a winning acknowledgement.
4. **Decide whether manual device preference is needed:** an accessibility-oriented “use this PC for the next hour/until changed” override may be more reliable than inferred activity, but is not required for the initial heuristic.

