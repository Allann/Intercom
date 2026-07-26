// PROTOTYPE for issue #15 — "does the AudioGraph + Opus candidate deliver
// acceptable push-to-talk/group latency and loss behavior, and can hands-free
// avoid unusable echo?"
//
// The echo and true end-to-end latency/CPU questions require real microphones,
// real speakers, and real household PC hardware — they cannot be answered from
// this environment. See README.md for the exact two-PC hardware test plan that
// still has to run tomorrow.
//
// What CAN be validated without hardware is the jitter-buffer / packet-loss-
// concealment LOGIC the audio research doc specifies: 20ms Opus frames, a 60ms
// (3-frame) starting target that adapts within a bounded range, PLC on a missing
// frame instead of waiting, and discard-old-not-grow-latency on overflow. This
// is a synthetic-network simulator for exactly that logic.
//
// Run: dotnet run
// Then type 'help'. State prints after every command.

using System;
using System.Collections.Generic;
using System.Linq;

var sim = new JitterSim();
Console.WriteLine("Jitter-buffer / PLC logic prototype (issue #15). Type 'help' for commands.");
sim.PrintState();

while (true)
{
    Console.Write("\n> ");
    var line = Console.ReadLine();
    if (line is null) break;
    line = line.Trim();
    if (line.Length == 0) continue;
    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    var cmd = parts[0].ToLowerInvariant();
    if (cmd is "quit" or "exit") break;

    try { sim.Handle(cmd, parts); }
    catch (Exception ex) { Console.WriteLine($"! error: {ex.Message}"); }
}

enum FrameOutcome { PlayedOnTime, Concealed, DiscardedOld }

sealed class JitterSim
{
    const int FrameMs = 20;      // Opus frame size (research doc default)
    const int MinTargetMs = 40;  // 2 frames — bounded adaptive floor (illustrative)
    const int MaxTargetMs = 120; // 6 frames — bounded adaptive ceiling (illustrative)
    const int StartTargetMs = 60; // 3 frames — research doc's starting point
    const int AdaptWindowFrames = 25; // rolling window for adapting the target
    const int OverflowHeadroomFrames = 3; // extra frames of buffer before we discard-old

    readonly Random _rng = new(12345); // fixed seed: reproducible runs

    int _targetMs = StartTargetMs;
    int _lossPct = 2;
    int _jitterMinMs = 0;
    int _jitterMaxMs = 40;

    int _nextCaptureSeq = 0;
    int _wallClockMs = 0;      // real elapsed time (capture side)
    int _nextExpectedSeq = 0;  // playout side: which frame is due next, once playback has started
    bool _playbackStarted = false;

    // seq -> arrival time in ms (null = will never arrive / lost), keyed by capture seq
    readonly Dictionary<int, int?> _pending = new();

    readonly List<FrameOutcome> _recentOutcomes = new();
    int _totalPlayed = 0, _totalConcealed = 0, _totalDiscardedOld = 0;

    public void Handle(string cmd, string[] parts)
    {
        switch (cmd)
        {
            case "help": PrintHelp(); return;

            case "config":
                ConfigureChannel(parts);
                break;

            case "send":
                int count = parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 1;
                Send(count);
                break;

            case "burst-loss":
                int burst = parts.Length > 1 && int.TryParse(parts[1], out var b) ? b : 5;
                SendBurstLoss(burst);
                break;

            case "flood":
                // Simulate a burst-delivery event: several already-captured frames that
                // were held up in transit (e.g. a bufferbloated Wi-Fi hop) all arrive at
                // once, right now, instead of spread out. This is the realistic way an
                // "arrived faster than we can play it" overflow actually happens.
                int floodCount = parts.Length > 1 && int.TryParse(parts[1], out var fc) ? fc : 8;
                Flood(floodCount);
                break;

            case "tick":
                int frames = parts.Length > 1 && int.TryParse(parts[1], out var f) ? f : 1;
                for (int i = 0; i < frames; i++) Tick();
                break;

            case "run":
                // convenience: send n packets then immediately try to play n frames,
                // the way a live call actually behaves (arrival and playout interleaved)
                int m = parts.Length > 1 && int.TryParse(parts[1], out var r) ? r : 20;
                for (int i = 0; i < m; i++) { Send(1); Tick(); }
                break;

            case "stats":
                break; // just reprint

            default:
                Console.WriteLine($"! unknown command: {cmd} (try 'help')");
                return;
        }

        PrintState();
    }

    void PrintHelp()
    {
        Console.WriteLine("""
            Commands:
              config loss=<pct> jittermin=<ms> jittermax=<ms>
                                    set the synthetic network model (default: loss=2 jittermin=0 jittermax=40)
              send [n]              capture+transmit n more 20ms frames under the current network model
              burst-loss [n]        force the next n frames to be lost outright (simulate a burst)
              flood [n]             n already-captured frames arrive all at once right now (bufferbloat burst)
              tick [n]              advance the playout clock by n frames (default 1), playing/concealing as needed
              run [n]               convenience: interleave send+tick n times, like a live call
              stats                 reprint current counters
              quit
            """);
    }

    void ConfigureChannel(string[] parts)
    {
        foreach (var p in parts.Skip(1))
        {
            var kv = p.Split('=', 2);
            if (kv.Length != 2) continue;
            switch (kv[0].ToLowerInvariant())
            {
                case "loss": _lossPct = int.Parse(kv[1]); break;
                case "jittermin": _jitterMinMs = int.Parse(kv[1]); break;
                case "jittermax": _jitterMaxMs = int.Parse(kv[1]); break;
            }
        }
        Console.WriteLine($"Channel model: loss={_lossPct}% jitter=[{_jitterMinMs},{_jitterMaxMs}]ms");
    }

    void Send(int count)
    {
        for (int i = 0; i < count; i++)
        {
            int seq = _nextCaptureSeq++;
            int captureTimeMs = seq * FrameMs;
            bool lost = _rng.Next(100) < _lossPct;
            if (lost)
            {
                _pending[seq] = null;
            }
            else
            {
                int delay = _jitterMaxMs > _jitterMinMs
                    ? _rng.Next(_jitterMinMs, _jitterMaxMs + 1)
                    : _jitterMinMs;
                _pending[seq] = captureTimeMs + delay;
            }
        }
    }

    void Flood(int count)
    {
        for (int i = 0; i < count; i++)
        {
            int seq = _nextCaptureSeq++;
            _pending[seq] = _wallClockMs; // "arrives" right now, regardless of capture time
        }
        Console.WriteLine($"{count} frames arrived in a single burst at wall clock {_wallClockMs}ms.");
    }

    void SendBurstLoss(int count)
    {
        for (int i = 0; i < count; i++)
        {
            int seq = _nextCaptureSeq++;
            _pending[seq] = null; // guaranteed lost, regardless of configured loss%
        }
        Console.WriteLine($"Forced {count} consecutive frames lost (burst).");
    }

    void Tick()
    {
        _wallClockMs += FrameMs;

        // Startup buffering: a real jitter buffer doesn't start playing the instant
        // frame 0 is captured — it waits `target` ms to build a cushion first. Model
        // that as a fixed startup delay based on the STARTING target; growing/shrinking
        // the target later only changes overflow headroom, not a second startup wait
        // (a simplification — see README).
        if (!_playbackStarted)
        {
            if (_wallClockMs < StartTargetMs) return; // still filling the initial buffer, nothing to play/conceal yet
            _playbackStarted = true;
        }

        int expectedSeq = _nextExpectedSeq;

        // Overflow check: how many frames have already ARRIVED but are still sitting
        // unplayed, waiting their turn (seq >= what's about to play)? If that backlog
        // exceeds our headroom, discard the OLDEST arrived-but-unplayed frame rather
        // than let latency grow (per the audio research doc's rule).
        var arrivedBacklog = _pending
            .Where(kv => kv.Key >= expectedSeq && kv.Value is int at && at <= _wallClockMs)
            .OrderBy(kv => kv.Key)
            .ToList();

        int capacityFrames = _targetMs / FrameMs + OverflowHeadroomFrames;
        while (arrivedBacklog.Count > capacityFrames)
        {
            var oldest = arrivedBacklog[0];
            _pending.Remove(oldest.Key);
            arrivedBacklog.RemoveAt(0);
            _totalDiscardedOld++;
            Record(FrameOutcome.DiscardedOld);
            if (oldest.Key == expectedSeq)
            {
                expectedSeq++;
                _nextExpectedSeq++;
            }
        }

        FrameOutcome outcome;
        if (_pending.TryGetValue(expectedSeq, out var arrival) && arrival is int at2 && at2 <= _wallClockMs)
        {
            outcome = FrameOutcome.PlayedOnTime;
            _totalPlayed++;
        }
        else
        {
            // Missing, lost, or simply hasn't arrived by its playout deadline.
            // Never wait indefinitely for it — conceal and move on.
            outcome = FrameOutcome.Concealed;
            _totalConcealed++;
        }
        _pending.Remove(expectedSeq);
        _nextExpectedSeq = expectedSeq + 1;
        Record(outcome);
        AdaptTarget();
    }

    void Record(FrameOutcome outcome)
    {
        _recentOutcomes.Add(outcome);
        if (_recentOutcomes.Count > AdaptWindowFrames)
            _recentOutcomes.RemoveAt(0);
    }

    void AdaptTarget()
    {
        if (_recentOutcomes.Count < AdaptWindowFrames) return;
        double concealRate = _recentOutcomes.Count(o => o == FrameOutcome.Concealed) / (double)_recentOutcomes.Count;

        if (concealRate > 0.10 && _targetMs < MaxTargetMs)
        {
            _targetMs += FrameMs;
            Console.WriteLine($"  [adapt] concealment rate {concealRate:P0} over last {AdaptWindowFrames} frames -> " +
                              $"growing target to {_targetMs}ms (bounded max {MaxTargetMs}ms)");
        }
        else if (concealRate == 0 && _targetMs > MinTargetMs)
        {
            _targetMs -= FrameMs;
            Console.WriteLine($"  [adapt] 0% concealment over last {AdaptWindowFrames} frames -> " +
                              $"shrinking target to {_targetMs}ms (bounded min {MinTargetMs}ms)");
        }
    }

    public void PrintState()
    {
        int occupancyFrames = _pending.Count(kv => kv.Key >= _nextExpectedSeq && kv.Value is int at && at <= _wallClockMs);
        Console.WriteLine("----------------------------------------------------------------");
        Console.WriteLine($" wall clock={_wallClockMs}ms | playback started={_playbackStarted} | next expected seq={_nextExpectedSeq}");
        Console.WriteLine($" target={_targetMs}ms ({_targetMs / FrameMs} frames) | buffered-and-waiting={occupancyFrames} " +
                          $"| pending in flight={_pending.Count}");
        Console.WriteLine($" channel: loss={_lossPct}% jitter=[{_jitterMinMs},{_jitterMaxMs}]ms");
        Console.WriteLine($" played on time={_totalPlayed}  concealed(PLC)={_totalConcealed}  discarded-old(overflow)={_totalDiscardedOld}");
        if (_totalPlayed + _totalConcealed > 0)
        {
            double rate = _totalConcealed / (double)(_totalPlayed + _totalConcealed);
            Console.WriteLine($" lifetime concealment rate={rate:P1}");
        }
        Console.WriteLine("----------------------------------------------------------------");
    }
}
