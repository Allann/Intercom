# MVP Specification — Local-First Family Intercom

Synthesizes every decision closed under [issue #1](https://github.com/Allann/Intercom/issues/1) (the wayfinder map) into one implementation-ready specification, per [issue #13](https://github.com/Allann/Intercom/issues/13). This document is the specification; `docs/adr/` holds the reasoning and rejected alternatives behind each locked decision, and `CONTEXT.md` is the canonical glossary — terms below are used exactly as defined there.

This spec stops before implementation. See the bottom of this document for the build-ticket decomposition that turns it into work.

## 1. Product shape and constraints

A Windows 11 x64, packaged WinUI 3 desktop app for a small trusted **family** — a small approved group of people and devices, potentially spread across multiple households connected by a private network. No cloud accounts, no central server, no Windows service, no internet NAT traversal or relay infrastructure. The app is **resident**: it launches at sign-in, lives in the system tray, and stays reachable while its window is closed, ending only at sign-out or explicit quit.

First tracer scope: two **local peers** on one LAN. VPN peer entry (manual IP/hostname) is deliberately the last MVP slice, added only once pure-local behavior is solid.

Out of scope for the whole MVP: video, child-monitor mode, emergency DND override, cloud/free-form speech-to-text, communication before sign-in or after sign-out.

## 2. Protocol architecture (ADR-0001)

Three deliberately separate channels:

- **Discovery** — mDNS/DNS-SD (`_intercom._tcp.local`), advertising only a protocol version and a non-secret peer-ID hint. Discovery is visibility only, never trust: an unapproved, discovered peer cannot open audio, send chat, or trigger an attention card.
- **Control channel** — one mutually-authenticated TLS-over-TCP connection per peer, carrying pairing traffic, presence, chat, attention cards, group-floor coordination, and capability negotiation.
- **Voice channel** — direct unicast UDP per live audio stream, authenticated and encrypted independently of the control channel (AES-256-GCM, per-stream keys).

**Connection lifecycle.** One heartbeat mechanism total: the presence lease (10s heartbeat, 30s lease, jittered to avoid synchronized traffic) doubles as connection liveness — no separate transport keepalive. Both the initial connection race and any post-drop reconnect resolve via the same deterministic rule: lower SPKI hash initiates as TLS client, higher waits/accepts, so exactly one logical channel ever survives.

**Capability negotiation.** A one-time `Hello` (protocol version + static capability set: `text`, `send_audio`, `receive_audio`, `tts`, `spoken_chat`, `group_floor`, `attention_cards`) is exchanged once per new control-channel connection, distinct from the presence lease's transient per-contact capability field.

**Delivery semantics.** Every message with a message ID gets an automatic `Delivered` receipt on arrival — mechanical, no human involved. `Acknowledged` is sent only for attention cards, when a human actually acts on it; plain chat has delivery only, no read receipt. No auto-resend across a dropped connection, even a brief one: an in-flight unacknowledged message is failed the instant the connection drops, and the sender retries manually.

**Group coordination.** The peer who starts a group voice conversation is its first **floor coordinator**. If the coordinator leaves, every remaining participant deterministically recomputes the same replacement (lowest stable peer ID among active participants) — no manual claim, no election race. The raise-hand queue is fully broadcast to every participant, so a new coordinator is always already caught up. Audio sessions between participant pairs are negotiated once at group join and sit idle until granted the floor; a floor grant is a pure control-plane flag, so hand-offs are instant. A successful **interrupt request** grants the floor directly to the interrupter, bypassing the queue. The session ends only when the last participant leaves, plus an explicit "End Session for Everyone" action for the current coordinator.

**VPN extension seam.** VPN peers are ordinary peers on the identical protocol — they just skip mDNS and get their TCP endpoint entered manually. Identity stays pinned by SPKI hash regardless of address changes; a stale manual address fails visibly without invalidating the pairing.

## 3. Pairing and trust model (ADR-0002)

Each peer generates one long-lived ECDSA P-256 self-signed identity certificate on first run (**10-year validity, no MVP renewal path** — expiry is handled identically to identity loss: new identity, re-pair everywhere). Reliable connections use mutual TLS; approval pins the peer by SHA-256 hash of its SPKI, never by friendly name, IP, or certificate subject.

**Pairing ceremony**: both peers display the same six-digit numeric SAS (SHA-256 over the canonical, nonce-fresh transcript, rejection-sampled to `000000`-`999999`), and both people must explicitly confirm the codes match before either side becomes an **approved peer**. The Postcard Badges UI ([issue #7](https://github.com/Allann/Intercom/issues/7)) displays this value formatted `NNN NNN` inside a wax-seal graphic — no separate encoding. A pairing request expires after **2 minutes** with no trust change; an explicit reject is also supported, sent immediately over the still-open pairing connection. Rate limiting: one outstanding request per peer identity, 5 attempts per source per 10 minutes, 20 attempts globally per 10 minutes — this only throttles new pairing attempts, not existing approved-peer traffic.

**Revocation ("forget peer")** is local and immediate: terminate active sessions, remove the SPKI pin/contact association, delete pending session keys and replay state. A best-effort `Forgotten` notice is sent only if the connection happens to be live at that instant — no guaranteed delivery. Forgetting is asymmetric and must stay visible in the UI: the other side remains approved until it independently forgets you too.

**Re-pairing / identity change**: a pin mismatch on an approved peer never falls back to pairing within the same connection — the UI shows "identity changed" and requires an explicit forget/re-pair action. A lost or corrupted local identity generates a new one and requires re-pairing with everyone.

Identity storage uses Windows DPAPI, `CurrentUser` scope.

## 4. Presence, DND, and multi-device routing (ADR-0004)

Two independent dimensions per device: **availability** (`available` / `idle` / `unavailable`) and **interruption mode** (`normal` / **do-not-disturb**). `offline` is never self-announced; peers infer it from lease expiry.

Availability is derived from `GetLastInputInfo` for the current signed-in session only (never raw keystroke content), polled locally every **30 seconds**. Promotion to `available` on new input is instant; demotion to `idle` (at the 2-minute idle threshold) requires **two consecutive idle samples** to avoid boundary flapping. Session lock/disconnect/suspend/shutdown make the endpoint `unavailable` immediately; resume/unlock re-evaluates from scratch rather than assuming reachability.

Do-not-disturb is an explicit, locally stored setting, never inferred from inactivity or calendar/focus state. In DND: voice, hands-free requests, attention chimes, and interrupt requests stay silent/queued; text is still delivered, silently.

**Routing for a contact with multiple approved peers:**

- **Device-to-contact association** is purely local and unilateral — each device groups its own already-approved peers under a contact with no coordination from the other side, and carries zero trust implication (approval remains exactly per-peer).
- **Live voice** (1:1 or Quick Chat) routes to exactly one highest-ranked non-DND device; a **tie** (same availability, same idle bucket, no distinguishable recency within the same heartbeat window) is broken deterministically by stable device ID — never two devices ringing for one call.
- **Text and attention cards**: send to the top-ranked device first; if no mechanical `Delivered` receipt arrives within **3 seconds**, or devices are tied, fan out to every live eligible device. First human acknowledgement wins; the sender broadcasts `resolved(interaction_id)` to withdraw sibling notifications. Each fanned-out attention card also carries its own **5-minute local expiry**, independent of the sender, so a sender that goes offline mid-relay can't leave a stale duplicate forever.
- **Manual device override**: an explicit "prefer this device for me right now" toggle, acting as a hard override that outranks activity-based ranking entirely for that contact, lapsing automatically if the device becomes unavailable. Included in MVP for people whose activity can't be reliably inferred.
- "Follow me" only affects **new** interactions — an established voice stream or open conversation never silently migrates.

## 5. Voice pipeline (research #3, prototyped in #15)

`Windows.Media.Audio.AudioGraph`, shared mode, communications category, standard (non-raw) signal processing. Network audio is mono 48kHz Opus, VoIP application mode, one 20ms frame per datagram, starting at 24kbit/s VBR.

Jitter buffer starts at a **60ms (3-frame) target**, adapting within a bounded range (illustrative bounds 40-120ms validated in the #15 prototype) based on measured concealment rate — grows when recent concealment exceeds ~10%, shrinks when a window is clean. A late or missing frame invokes Opus packet-loss concealment and playout continues; it is never retransmitted. On buffer overflow (a burst-delivery event), the **oldest** arrived-but-unplayed frame is discarded rather than letting latency grow.

The same pipeline serves all voice modes:
- **Push-to-talk**: momentary, starts/stops capture+transmission on hold/release.
- **Hands-free session**: latched two-way, active until either peer ends it. **Acoustic echo over speakers (not headsets) is a hard implementation gate** — must be validated on real kitchen-style hardware before hands-free ships; if unacceptable, require a headset or add a dedicated AEC stage.
- **Group voice**: encode once, fan out the same packet directly to every participant; the floor protocol guarantees only one transmitter, so no mixing is needed.

Audio datagrams are authenticated/encrypted independently of the control channel (AES-256-GCM, fresh key per sender/stream epoch, exchanged over the authenticated control channel first).

## 6. Global input and attention presentation (prototyped in #16)

Push-to-talk uses `RegisterHotKey` for the initial press, plus a narrowly-scoped `GetAsyncKeyState` poll on just the configured key for release detection (the least invasive of the three candidates the research considered) — validated as a real, runnable spike in #16, pending a live human press/release test and a comparison against raw input / a `WH_KEYBOARD_LL` hook before being treated as final.

Attention cards default to **native Windows toast notifications** (`AppNotificationManager`) rather than a custom always-on-top popup window — durable, appears in Action Center, respects accessibility/Focus Sessions automatically. The 320×320 illustration is an asset source size, not a guaranteed rendered square; a custom popup remains a fallback only if the exact comic-card visual is later judged essential.

## 7. Shell UI (prototyped in #7, #8, #9)

The **Console Panel** design ([issue #8](https://github.com/Allann/Intercom/issues/8)) is the locked shell layout: a family presence rail (lamp + role badges: 🎙️ floor, ✋ queue position, 👑 coordinator, 🤝 hands-free), a large push-to-talk dial enabled only while holding the floor or a hands-free session, a hands-free control kept visually and functionally separate from the group voice floor panel, a flippable Available/DND sign, a chat drawer, and an attention-card shelf with a composer (preset or custom purpose text, emoji icon picker, recipient select).

The group voice floor panel ([issue #9](https://github.com/Allann/Intercom/issues/9)) surfaces: current speaker, a joinable/leavable raise-hand queue, an Interrupt action, and coordinator-only Grant-Floor/End-Session controls that appear only when the local peer currently holds the coordinator role.

"+ Add Family Member" opens the Postcard Badges pairing flow ([issue #7](https://github.com/Allann/Intercom/issues/7)) as a modal over the shell: illustrated per-peer postcards with wax-seal code matching, stamp-to-approve / tear-up-to-reject actions, and a Rolodex of already-approved peers for renaming and revocation.

## 8. Installation, startup, and updates (ADR-0003)

Self-signed identity certificate, sideloaded MSIX, trusted to `Cert:\CurrentUser\TrustedPeople` (no administrator elevation required). Launch-at-sign-in is enabled automatically during first-run setup with no yes/no prompt — a passive confirmation line only, toggleable afterward in Settings, always reflecting actual OS state honestly.

Closing the main window hides it to the tray; an explicit tray action quits. One combined, Teams-style onboarding screen requests microphone access and notification permission together, with LAN discovery deliberately started at that same moment so the Windows Firewall private-network prompt clusters into the same first-run moment. The app's settings deep-link to the relevant OS Settings pages for later review and reflect actual granted/denied state.

Updates are manual: a lightweight in-app check against a static version source (e.g. GitHub Releases) surfaces a passive "update available" notice only, never auto-downloading or auto-installing; applying it means re-running the new signed MSIX, which upgrades in place under the same package identity.

Crash recovery is manual-relaunch-only — no watchdog or auto-restart — with a simple "closed unexpectedly last time" notice on next launch for visibility.

## 9. Domain glossary

See `CONTEXT.md` for the authoritative, evolving glossary. Key terms used throughout this spec: Peer, Contact, Approved peer, Family, Resident app, Local peer, VPN peer, Conversation, Spoken chat, Push-to-talk, Hands-free session, Voice floor, Raise hand, Floor coordinator, Interrupt request, Attention card, Interruption mode, Do-not-disturb, Pairing ceremony, Verification code, Preferred device override.

## 10. Build ticket decomposition

The following vertical slices, in dependency order, turn this specification into implementation work. Each is tracked as its own GitHub issue with native blocking dependencies; see the issue tracker for the live graph. Ordering ends with manual VPN peer entry, per this document's own scope note in §1.

1. App shell scaffolding, packaging, and tray residency
2. Local peer identity and secure storage
3. Local discovery (mDNS/DNS-SD)
4. Reliable control channel (mutual TLS, heartbeat/lease, reconnect)
5. Pairing ceremony (protocol + Postcard Badges UI)
6. Presence and do-not-disturb
7. Text chat
8. Attention cards (composer, delivery, native toast)
9. Audio pipeline foundation (AudioGraph, Opus, jitter buffer, UDP transport)
10. Push-to-talk (one-to-one)
11. Hands-free session (one-to-one)
12. Group voice floor
13. Multi-device contact routing
14. Manual update check
15. VPN peer manual entry (final slice)
