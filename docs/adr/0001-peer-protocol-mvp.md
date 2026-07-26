# Lock the MVP peer protocol: boundaries, lifecycle, negotiation, delivery, group coordination, and the VPN seam

Resolves [issue #10](https://github.com/Allann/Intercom/issues/10), building on the networking, audio, security, and presence research (`docs/research/`).

## Decision

**Protocol boundary.** Three channels, deliberately kept separate: mDNS/DNS-SD for discovery only (never trust — visible and spoofable by anyone on the LAN), one mutually-authenticated TLS-over-TCP connection per peer carrying all reliable state (pairing, presence, chat, attention cards, group-floor control, capability negotiation), and direct unicast UDP with AES-GCM per live voice stream. WebSockets and WebRTC/QUIC were considered and rejected/deferred (see `docs/research/local-network-transport.md`).

**Connection lifecycle.** One unified heartbeat: the presence lease (10s heartbeat, 30s lease, jittered) doubles as connection liveness — no separate transport-level keepalive. Both initial connection races and post-drop reconnects resolve with the same deterministic tie-break (lower SPKI hash initiates, higher waits/accepts), so there is never a duplicate logical channel.

**Capability negotiation.** A one-time `Hello` (protocol version + static capability set) is exchanged once per new control-channel connection, right after mutual TLS succeeds. This is distinct from the presence lease's `capabilities` field, which reflects transient per-contact routing state, not protocol support.

**Delivery semantics.** Every message with a message ID gets an automatic, app-generated `Delivered` receipt the instant it's accepted — no human involved. `Acknowledged` is sent only when a human acts (attention cards only; plain chat has no read receipt). There is no auto-resend across a dropped connection, even a brief one: an in-flight unacknowledged message is treated as failed the moment the connection drops, and the sender must retry manually. This keeps "delivered" meaning something precise.

**Group coordination.** The peer who starts a group conversation is its first floor coordinator. If the coordinator leaves, every remaining participant deterministically recomputes the same replacement (lowest stable peer ID among active participants) — no manual claim, no election race. The raise-hand queue is fully broadcast to every participant rather than held privately by the coordinator, so a new coordinator (or anyone, really) is always already caught up. Audio sessions between participant pairs are negotiated once at group join and sit idle until granted the floor; a floor grant is a pure control-plane flag, not a renegotiation, so hand-offs are instant. A successful interrupt grants the floor directly to the interrupter, bypassing the queue — that's what distinguishes "interrupt" from "raise hand with priority." The session ends only when the last participant leaves; the current coordinator additionally has an explicit "End Session for Everyone" action.

**VPN extension seam.** VPN peers are not a different protocol tier. They reuse every rule above unchanged, they just skip mDNS discovery and get their TCP endpoint entered manually. That manual address is treated as a "last known endpoint" — reconnection retries against it and can fail visibly without invalidating the pairing, since identity is pinned by SPKI hash, not by address.

## Why

Each piece was chosen to avoid inventing a second version of a rule we already needed elsewhere: one heartbeat instead of two, one deterministic tie-break reused for both initial connection and reconnects, one capability exchange instead of overloading presence. The group-coordination design specifically avoids any server-like arbitration role — coordinator assignment, handoff, and queue state are all things every peer can compute identically from broadcast facts, which fits the explicit no-central-server constraint on this project.

## Considered and rejected

- **Manual "claim coordinator" UI** (prototyped in the shell) — rejected in favor of automatic deterministic reassignment, since a manual claim is a real race with no server to arbitrate it.
- **Coordinator-private queue** — rejected because it would require an explicit state-transfer step on handoff; full broadcast means a new coordinator is already caught up for free.
- **Per-floor-grant audio renegotiation** — rejected as adding a round-trip of latency to every hand-off and every interrupt, which undermines the point of "interrupt" being fast.
- **Queue-mediated interrupt** (interrupter just jumps the queue, coordinator still grants) — rejected as functionally indistinguishable from a high-priority raise-hand, which would make "interrupt" a redundant concept.
- **Auto-resend on reconnect** — rejected because it blurs "delivered" into "eventually maybe delivered on some connection," and requires a retry-dedup mechanism that isn't otherwise needed yet.
