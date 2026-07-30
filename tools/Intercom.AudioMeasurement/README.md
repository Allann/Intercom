# Two-PC audio measurement harness

This harness instruments Intercom's real AudioGraph → Opus → encrypted UDP →
adaptive jitter buffer → speaker-queue path. It does not substitute a synthetic
audio stack and does not claim to sense the physical instant a speaker cone
becomes audible.

## Run a profile

1. Synchronise both PCs in **Settings → Time & language → Date & time → Sync now**.
2. Install the same Intercom build on both PCs.
3. On both PCs, open **Intercom Settings → Two-PC audio measurement** and choose
   the same profile: Clean, 1%, 5%, or Six-frame burst loss.
4. End any existing voice session and start a new one so the profile is loaded.
5. From the sender, perform at least ten PTT bursts of five seconds each, with
   two seconds silence between bursts. Confirm audibility at the receiver.
6. Set the profile back to **Disabled (normal audio)** when finished.
7. Copy `%LOCALAPPDATA%\Intercom\logs\intercom.log` from each PC to one machine.

Generate the report from the repository root. Each input may be one log file or
a directory containing its rolled `intercom*.log` files:

```powershell
dotnet run --project tools\Intercom.AudioMeasurement -- `
  report .\sender-intercom.log .\receiver-intercom.log clean .\report-clean.md
```

Repeat for `loss1`, `loss5`, and `burst`. Attach the four Markdown reports to
GitHub issue #40. Threshold failures should be filed as focused bug issues.

## Measurements

- press to first frame queued to the receiver's speaker;
- per-frame capture-to-speaker-queue latency (min/median/p95/max);
- release marker to drained speaker queue;
- deliberately dropped, concealed, and discarded frames;
- maximum adaptive jitter target/occupancy observed.

The report carries an explicit clock-sync caveat because cross-PC values use
UTC. The sender's timestamp is carried in the authenticated audio packet; the
receiver's observation is written locally, so Windows clock offset contributes
directly to the reported latency.
