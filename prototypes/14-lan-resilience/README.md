# Prototype: LAN discovery & connection resilience (issue #14)

## What this is

Issue #14 asks whether the packaged candidate reliably discovers, connects, and
recovers across IPv4/IPv6, multiple adapters, sleep, Wi-Fi changes, app restart,
and stale discovery records, on clean Windows 11 PCs with Private/Public
firewall profiles.

That real question needs two actual PCs, real Windows Firewall prompts, real
mDNS traffic, and real sleep/Wi-Fi hardware events — none of which this
environment can produce autonomously overnight.

What *can* be validated without hardware is whether the **connection-lifecycle
state machine we locked in ADR-0001** (`docs/adr/0001-peer-protocol-mvp.md`)
actually holds together under every hard case the research doc calls out:
simultaneous connection races, dropped connections, sleep/resume, Wi-Fi
address changes, local process restart, and remote peer restart with stale
heartbeats. This prototype is that state machine, runnable and interactive,
on a simulated clock.

## Run it

```
cd prototypes/14-lan-resilience
dotnet run
```

Type `help` for the command list. The full state prints after every command,
per the standard prototype rule of surfacing state on every action.

## State modeling

`ConnState` is a closed type hierarchy (one record per state), not an enum
plus loose fields — following the
[`making-illegal-states-unrepresentable`](file:///M:/allan-skills/skills/engineering/making-illegal-states-unrepresentable/SKILL.md)
approach. Two things this fixed for real, not just hypothetically:

- **Heartbeat timestamps live only on `Connected`/`Reconnecting`.** There's no
  nullable `LastHeartbeatAtSecond` field sitting on the whole simulation that
  happens to be meaningless in four of the six states.
- **`Asleep(Was)` is a distinct state that wraps what it interrupted**, not a
  separate `bool _localAsleep` living alongside `_state`. The first version of
  this prototype had exactly that bug: `sleep` set the bool but moved `_state`
  to `Reconnecting`, and a subsequent `wifi-change` command only checked
  `_state == Connected` and never looked at the bool — so `wifi-change` while
  asleep silently produced `Discovered` with `_localAsleep` still `true`, an
  incoherent combination that should never exist. Folding sleep into the state
  type made that combination impossible to construct: `Asleep` doesn't
  implement `IAwakeState`, so every other transition's pattern match on
  `IAwakeState` simply doesn't match while asleep, and `wifi-change`/`connect`/
  etc. now correctly print `'wifi-change' ignored: the local PC is asleep`
  instead of silently corrupting state.

## What it proves

- Discovery is visibility only — `discover` never jumps straight to `Connected`.
- The presence lease doubles as connection liveness — a connection with no
  heartbeat for >30s (simulated) drops to `Reconnecting` on its own, no
  separate keepalive needed.
- The same deterministic SPKI-hash tie-break resolves both a genuine race
  (`race`) and every reconnect attempt after a drop — never two logical
  channels.
- A restarted peer's new incarnation supersedes the old one; a heartbeat
  claiming the stale incarnation (`heartbeat-stale`) is rejected outright.
- Sleep never claims to stay `Connected`; resume goes back through
  `Discovered`/`connect` rather than assuming reachability.
- Wi-Fi/address changes invalidate the *reachability path* (back to
  `Discovered`) without touching the approved-peer identity/trust — that's
  a separate, unaffected concern in the real implementation (the SPKI pin).

## What this does NOT prove — still needs real hardware

Straight from the transport research doc's required test list. None of these
are satisfied by this prototype:

1. Two packaged WinUI 3 peers actually advertising/browsing/connecting on
   clean Windows 11 machines with no special install, on both Private and
   Public firewall profiles — including what prompts/rules actually appear.
2. IPv4-only, IPv6 dual-stack, and simultaneous Ethernet+Wi-Fi behavior.
3. Real sleep/resume and real Wi-Fi roaming/adapter changes, not simulated ones.
4. Duplicate friendly names and multicast-disabled/client-isolated Wi-Fi.
5. Real app restart (process actually exits and relaunches) rather than a
   simulated incarnation swap.

Treat this prototype as validating the *shape* of the state machine so the
real two-PC hardware pass has a specification to test against, not as a
replacement for that pass.
