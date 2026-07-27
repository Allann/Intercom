# Lock multi-device contact routing edge cases

Resolves [issue #17](https://github.com/Allann/Intercom/issues/17), building on the active-device-presence research (`docs/research/active-device-presence.md`), which had already designed the ranking/routing model but left several specific numbers and mechanisms as explicit follow-up work.

## Decision

**Hysteresis sampling.** Idle state is polled locally every **30 seconds**; demoting from `available` to `idle` requires two consecutive idle samples (so at least 30-60 seconds past the 2-minute threshold), while promotion back to `available` on new input remains instant, unaffected by this cadence.

**Tie definition.** Two or more eligible devices are "tied" when they share the same availability, the same idle bucket, and have no distinguishable recency between them within the same heartbeat window. Live voice still forces a single choice via the existing stable-device-ID tiebreak (never ring two devices for one call); text and attention cards treat this same condition as insufficient confidence to pick one, and fan out instead.

**Initial-ack timeout.** **3 seconds** to receive the mechanical `Delivered` receipt (from ADR-0001) before a text/attention-card send widens from the single ranked device to every live eligible device for that contact.

**Duplicate-notification expiry.** Each fanned-out attention card carries its own **5-minute local expiry**, independent of the sender. This directly closes the research doc's flagged gap: the sender is only a temporary transaction coordinator relaying `resolved(interaction_id)`, and if it goes offline before relaying, sibling devices no longer depend on ever hearing from it — the card just drops locally on its own after 5 minutes.

**Manual device override.** Included in the MVP (the research doc had left it optional): a "prefer this device for me right now" toggle per device, acting as a hard override that outranks activity-based ranking entirely for that contact, not merely another ranking input. It lapses automatically if the device becomes unavailable, rather than persisting and silently misrouting later.

**Device-to-contact association.** Purely local and unilateral. Each device groups its own already-approved peers under a contact with no coordination or acknowledgement required from the peers being grouped, and nothing about the grouping is ever transmitted. This carries zero trust implication — approval remains exactly per-peer via the pairing ceremony (ADR-0002); removing a peer from a contact grouping only changes routing, never its approval status.

## Why

Each number here fills a gap the research doc explicitly flagged rather than reversing an existing decision — the ranking algorithm, lease timing, and general fan-out/dedup shape were already settled; this session just pinned the specific durations and mechanisms that were left as "prototype/decide later." The override being a hard override rather than a ranking input is the one substantive design choice: the whole premise of the feature is that automatic inference is wrong for that person right now, so it should not have to out-compete the very heuristic it's meant to override.

## Considered and rejected

- **Treating "prefer this device" as one more ranking input** rather than a hard override — rejected; it would still be possible for another device's activity signal to outrank an explicit human choice, defeating the point of an accessibility override.
- **A coordinated/bidirectional contact-association mechanism** (requiring the grouped peers to acknowledge or agree) — rejected; the research doc is explicit that this must stay routing metadata only, and a coordination requirement would blur that line and add protocol surface for no trust benefit.
- **Leaving duplicate-notification expiry undefined**, relying on the sender always successfully relaying `resolved()` — rejected; the research doc already identified the failure case (sender offline mid-relay), so leaving it unresolved would ship a known gap.
