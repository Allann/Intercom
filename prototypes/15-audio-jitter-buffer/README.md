# Prototype: jitter buffer / PLC logic (issue #15)

## What this is, and what it deliberately is NOT

Issue #15 asks two things:

1. Does AudioGraph + Opus deliver acceptable **push-to-talk and group fan-out
   latency/loss behavior** on the actual household PCs?
2. Can an accepted **hands-free session avoid unusable speaker-to-microphone
   echo** without extra hardware?

Neither of those can be answered from this environment. Both require real
microphones, real speakers, real household PCs, and — for echo specifically —
a human listening. There is no shortcut around that; see the hardware test
plan below, which still needs to run tomorrow.

What this prototype validates instead is the **jitter-buffer and packet-loss-
concealment logic** the audio research doc specifies: 20ms Opus frames, a
60ms (3-frame) starting target that adapts within a bounded range based on
measured concealment rate, PLC instead of waiting on a missing/late frame,
and discard-old-not-grow-latency on a burst-delivery overflow. It's a
synthetic-network simulator, not an audio engine — no actual sound, no
AudioGraph, no Opus codec involved.

## Run it

```
cd prototypes/15-audio-jitter-buffer
dotnet run
```

Type `help` for commands. State prints after every command.

Try, for example:

```
run 60                              # clean channel (2% loss default) — should settle near ~2-4% concealment
config loss=25 jittermin=0 jittermax=10
run 60                               # lossy channel — watch the target adapt upward
burst-loss 6
tick 6                                # a burst-loss event and its concealment
flood 12
tick 2                                # a bufferbloat-style burst delivery — watch discard-old fire
```

## What it caught

Building this surfaced a real bug worth noting: the first version had no
actual startup buffering delay before playout began, so it tried to play
frame 0 the instant it was captured — producing ~50% bogus concealment even
at 2% configured loss. Fixed by modeling a proper startup fill period before
playout starts, matching how a real jitter buffer behaves. Left as a reminder
that "the buffer target" has to actually delay playout, not just exist as a
number used for overflow bookkeeping.

## What it proves

- At a modest, realistic loss rate, concealment rate tracks loss rate
  closely rather than being amplified by a buffering bug.
- The adaptive target grows when recent concealment exceeds ~10% and shrinks
  back to the floor when a window is clean, always clamped to the bounded
  range — never grows unbounded even against a channel worse than the
  ceiling can compensate for (see the `jittermin=180` example in git history
  of this file's testing, where it correctly stays capped and keeps
  concealing rather than chasing an unreachable target).
- A burst-delivery event (`flood`) correctly triggers discard-of-the-oldest
  rather than letting the buffer — and therefore latency — grow unbounded.

## What this does NOT prove — still needs real hardware tomorrow

Straight from the audio research doc's validation gates. None of these are
satisfied by this prototype:

1. **Echo.** Test the actual kitchen-style hardware with speakers, not a
   headset, before accepting hands-free mode. This is a hard gate per the
   research doc — if echo is unacceptable, the fallback is a dedicated local
   AEC stage or requiring a headset for MVP hands-free.
2. **Real latency measurements**: press-to-first-audible-speech,
   release-to-silence, steady-state one-way latency, on the two-PC harness.
3. **Real loss behavior** at 1%, 5%, and short bursts — with real Wi-Fi/LAN,
   not a synthetic RNG.
4. **Device changes**: microphone/speaker unplug and default-device changes
   during each voice mode.
5. **Repeated start/stop** (100+ cycles) without stale audio, exceptions, or
   growing memory/handles.
6. **Simultaneous use** of the audio endpoint by another normal Windows app.
7. **CPU/allocation** while encoding once and fanning out to several
   loopback receivers — and the managed-vs-native Opus benchmark
   (Concentus vs libopus) the research doc calls for.

The 60ms/40-120ms/10%-threshold numbers used in this simulator are the
research doc's own stated defaults and illustrative bounds — the doc is
explicit that these are implementation defaults to tune from measurement,
not immutable product decisions. Treat any numbers coming out of this
simulator as a logic sanity check, never as a substitute for the real
latency/loss/echo numbers the hardware pass has to produce.
