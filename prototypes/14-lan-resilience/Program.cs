// PROTOTYPE for issue #14 — "does the connection-lifecycle state machine handle
// sleep, Wi-Fi changes, app restart, and stale discovery records correctly?"
//
// This is a LOGIC prototype, not a network prototype: it simulates the ADR-0001
// connection lifecycle (discovery -> connect -> connected -> reconnect -> offline,
// unified heartbeat/lease, deterministic SPKI tie-break, incarnation supersession)
// on a fake clock so you can drive every hard case interactively in one process.
//
// It does NOT touch real mDNS, real sockets, or the real Windows Firewall — the
// research doc's two-PC / Private+Public firewall / real Wi-Fi-adapter testing
// still has to happen on real hardware. See README.md in this folder for exactly
// what this prototype validates vs what still needs the two-PC hardware pass.
//
// Run: dotnet run
// Then type `help` for commands. State prints after every command.

using System;
using System.Collections.Generic;

var sim = new Simulation();
Console.WriteLine("LAN discovery/connection resilience prototype (issue #14). Type 'help' for commands.");
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

    try
    {
        sim.Handle(cmd, parts);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"! error: {ex.Message}");
    }
}

enum ConnState
{
    Idle,          // no discovery record, no connection
    Discovered,    // mDNS/DNS-SD record seen; NOT trusted, NOT connected
    Connecting,    // TLS handshake + Hello capability exchange in flight
    Connected,     // mutual TLS up, Hello exchanged, heartbeat/lease live
    Reconnecting,  // was Connected, lease/heartbeat lapsed or socket dropped, retrying
    Offline        // lease fully expired with no successful reconnect yet
}

sealed class Simulation
{
    // Fixed, illustrative SPKI hashes so the deterministic tie-break has something
    // concrete to compare. Lower hash = initiates as TLS client.
    const string LocalSpki = "3a7f...local";
    const string RemoteSpki = "9c10...remote";

    const int HeartbeatIntervalSeconds = 10; // matches ADR-0001
    const int LeaseSeconds = 30;             // matches ADR-0001

    int _clockSeconds = 0;
    ConnState _state = ConnState.Idle;

    Guid _localIncarnation = Guid.NewGuid();
    Guid _remoteIncarnation = Guid.NewGuid();
    int? _lastHeartbeatAtSecond = null;

    bool _localAsleep = false;
    string _note = "";

    public void Handle(string cmd, string[] parts)
    {
        switch (cmd)
        {
            case "help": PrintHelp(); return;

            case "discover": Discover(); break;
            case "expire-record": ExpireRecord(); break;

            case "connect": Connect(triggeredByRace: false); break;
            case "connect-ok": ConnectOk(); break;
            case "connect-fail": ConnectFail(); break;

            case "race": Race(); break;

            case "heartbeat": Heartbeat(stale: false); break;
            case "heartbeat-stale": Heartbeat(stale: true); break;

            case "tick":
                int seconds = parts.Length > 1 && int.TryParse(parts[1], out var s) ? s : 1;
                Tick(seconds);
                break;

            case "drop": Drop(); break;
            case "sleep": Sleep(); break;
            case "resume": Resume(); break;
            case "wifi-change": WifiChange(); break;
            case "restart": Restart(); break;
            case "remote-restart": RemoteRestart(); break;

            case "status": break; // just reprint state

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
              discover          mDNS/DNS-SD record for the peer becomes visible (discovery only, no trust)
              expire-record     the discovery record's TTL lapses / a goodbye record arrives
              connect           we initiate a TLS connection attempt to the discovered peer
              connect-ok        the TLS handshake + Hello capability exchange succeeds
              connect-fail      the TLS handshake attempt fails
              race              simulate BOTH sides dialing simultaneously (tie-break demo)
              heartbeat         a fresh, in-order presence-lease heartbeat arrives
              heartbeat-stale   a heartbeat arrives from an OLD incarnation/sequence (must be rejected)
              tick [n]          advance the simulated clock by n seconds (default 1)
              drop              the live TCP connection drops (network blip)
              sleep             this PC suspends (PBT_APMSUSPEND)
              resume            this PC resumes (PBT_APMRESUMEAUTOMATIC / user resume)
              wifi-change       network adapter/address changes (re-discovery required)
              restart           this app process restarts (new local incarnation)
              remote-restart    the REMOTE peer's process restarts (new remote incarnation)
              status            reprint current state
              quit              exit
            """);
    }

    // ---- discovery ----

    void Discover()
    {
        if (_state == ConnState.Idle)
        {
            _state = ConnState.Discovered;
            _note = "Discovery record seen. Peer is VISIBLE, not trusted, not connected.";
        }
        else
        {
            _note = $"Already past Discovered ({_state}); discovery record re-seen, no state change.";
        }
    }

    void ExpireRecord()
    {
        if (_state == ConnState.Discovered)
        {
            _state = ConnState.Idle;
            _note = "Discovery record TTL/goodbye expired before we ever connected. Back to Idle.";
        }
        else
        {
            _note = "Discovery record expiry is irrelevant once connected: the heartbeat/lease " +
                     "is the liveness signal now, not the mDNS record (ADR-0001).";
        }
    }

    // ---- connecting ----

    void Connect(bool triggeredByRace)
    {
        if (_state is not (ConnState.Discovered or ConnState.Reconnecting or ConnState.Offline))
        {
            _note = $"Nothing to connect to from state {_state}.";
            return;
        }
        _state = ConnState.Connecting;
        if (!triggeredByRace)
            _note = "TLS handshake attempt started.";
    }

    void Race()
    {
        // Both sides open a connection attempt at ~the same simulated instant.
        // ADR-0001: resolved deterministically by comparing stable peer IDs / SPKI hash.
        // Lower hash initiates as TLS client; the higher hash waits and accepts that
        // connection instead of completing its own outbound attempt.
        Connect(triggeredByRace: true);
        var localWins = string.CompareOrdinal(LocalSpki, RemoteSpki) < 0;
        _note = localWins
            ? $"Simultaneous connect attempts detected. Local SPKI ({LocalSpki}) < remote " +
              $"({RemoteSpki}) -> we win the tie-break and remain the initiator; the remote " +
              "side's outbound attempt is dropped in favor of ours. Exactly one logical channel survives."
            : $"Simultaneous connect attempts detected. Local SPKI ({LocalSpki}) > remote " +
              $"({RemoteSpki}) -> remote wins the tie-break; we abandon our outbound attempt and " +
              "accept theirs instead. Exactly one logical channel survives.";
    }

    void ConnectOk()
    {
        if (_state != ConnState.Connecting)
        {
            _note = $"connect-ok is only valid from Connecting (currently {_state}).";
            return;
        }
        _state = ConnState.Connected;
        _lastHeartbeatAtSecond = _clockSeconds;
        _note = "Mutual TLS up, one-time Hello capability exchange completed. " +
                "Connected. Heartbeat/lease clock starts now.";
    }

    void ConnectFail()
    {
        if (_state != ConnState.Connecting)
        {
            _note = $"connect-fail is only valid from Connecting (currently {_state}).";
            return;
        }
        _state = ConnState.Reconnecting;
        _note = "Handshake failed. Moving to Reconnecting; the deterministic tie-break rule " +
                "applies again on the next attempt (ADR-0001: same rule reused for reconnects).";
    }

    // ---- liveness ----

    void Heartbeat(bool stale)
    {
        if (_state is not (ConnState.Connected or ConnState.Reconnecting))
        {
            _note = $"Heartbeat received but there's no session to refresh (state {_state}). Ignored.";
            return;
        }

        if (stale)
        {
            _note = "Heartbeat carried an OLD incarnation/sequence. REJECTED: a changed incarnation " +
                     "resets the sequence, and a late packet from a superseded incarnation is never " +
                     "accepted as current (ADR-0001 / presence research). Lease NOT refreshed.";
            return;
        }

        _lastHeartbeatAtSecond = _clockSeconds;
        if (_state == ConnState.Reconnecting)
        {
            _state = ConnState.Connected;
            _note = "Fresh heartbeat arrived while reconnecting -> treated as reconnection success. Connected again.";
        }
        else
        {
            _note = "Fresh in-order heartbeat. Lease refreshed (this same signal doubles as connection liveness).";
        }
    }

    void Tick(int seconds)
    {
        _clockSeconds += seconds;
        if (_state == ConnState.Connected && _lastHeartbeatAtSecond is int last)
        {
            var age = _clockSeconds - last;
            if (age > LeaseSeconds)
            {
                _state = ConnState.Reconnecting;
                _note = $"{age}s since last heartbeat > {LeaseSeconds}s lease. Lease expired: " +
                         "this doubles as \"connection is dead,\" not just \"presence is stale\" " +
                         "(ADR-0001). Moving to Reconnecting.";
                return;
            }
        }
        if (_state == ConnState.Reconnecting && _lastHeartbeatAtSecond is int lastR)
        {
            var age = _clockSeconds - lastR;
            if (age > LeaseSeconds * 3) // no successful reconnect for a while -> give up to Offline
            {
                _state = ConnState.Offline;
                _note = "No successful reconnect for an extended period. Offline.";
                return;
            }
        }
        _note = $"Clock advanced by {seconds}s (now t={_clockSeconds}s).";
    }

    // ---- disruptions ----

    void Drop()
    {
        if (_state != ConnState.Connected)
        {
            _note = $"Nothing connected to drop (state {_state}).";
            return;
        }
        _state = ConnState.Reconnecting;
        _note = "TCP connection dropped (network blip). Any in-flight unacknowledged message is " +
                 "now FAILED — no auto-resend across this reconnect (ADR-0001). Moving to Reconnecting; " +
                 "both sides will retry using the same deterministic tie-break as initial connect.";
    }

    void Sleep()
    {
        _localAsleep = true;
        var wasConnected = _state == ConnState.Connected;
        _state = ConnState.Reconnecting;
        _note = wasConnected
            ? "Local PC suspending (PBT_APMSUSPEND). Best-effort 'about to be unavailable' is " +
              "advertised but not guaranteed delivered; moving to Reconnecting rather than staying Connected."
            : "Local PC suspending; nothing meaningfully connected to advertise.";
    }

    void Resume()
    {
        if (!_localAsleep)
        {
            _note = "Not asleep; resume is a no-op.";
            return;
        }
        _localAsleep = false;
        // Per presence research: resume does NOT jump straight back to Connected/available.
        // It must re-establish sockets/discovery and re-evaluate first.
        _state = ConnState.Discovered;
        _note = "Local PC resumed (PBT_APMRESUMEAUTOMATIC / user resume). Do NOT advertise available " +
                 "immediately — re-discover and reconnect from scratch. Back to Discovered; issue " +
                 "'connect' to re-establish.";
    }

    void WifiChange()
    {
        var wasConnected = _state == ConnState.Connected;
        _state = ConnState.Discovered;
        _note = wasConnected
            ? "Network adapter/address changed. Discovery must re-register/re-browse on the new " +
              "interface. The approved-peer identity (SPKI pin) is UNCHANGED by this — only the " +
              "reachability path is invalidated, not trust. Back to Discovered."
            : "Network adapter/address changed; re-evaluating from Discovered.";
    }

    void Restart()
    {
        _localIncarnation = Guid.NewGuid();
        _lastHeartbeatAtSecond = null;
        _state = ConnState.Idle;
        _note = $"Local process restarted. New local incarnation ({ShortGuid(_localIncarnation)}) " +
                 "supersedes the old one; nothing from the previous run can be accepted as current. Back to Idle.";
    }

    void RemoteRestart()
    {
        _remoteIncarnation = Guid.NewGuid();
        var wasConnected = _state == ConnState.Connected;
        _state = ConnState.Reconnecting;
        _note = wasConnected
            ? $"Remote peer restarted (new remote incarnation {ShortGuid(_remoteIncarnation)}). " +
              "Its sequence counter resets; any late packet claiming the OLD incarnation must be " +
              "rejected (see 'heartbeat-stale'). Moving to Reconnecting to re-establish under the new incarnation."
            : "Remote peer restarted; not currently connected to it anyway.";
    }

    // ---- output ----

    public void PrintState()
    {
        Console.WriteLine("----------------------------------------------------------------");
        Console.WriteLine($" t={_clockSeconds}s | state={_state} | asleep={_localAsleep}");
        Console.WriteLine($" local incarnation={ShortGuid(_localIncarnation)}  remote incarnation={ShortGuid(_remoteIncarnation)}");
        Console.WriteLine($" local SPKI={LocalSpki}  remote SPKI={RemoteSpki}  (lower wins tie-break)");
        Console.WriteLine($" last heartbeat at t={(_lastHeartbeatAtSecond?.ToString() ?? "never")}  " +
                          $"lease={LeaseSeconds}s  heartbeat-interval={HeartbeatIntervalSeconds}s");
        if (_note.Length > 0) Console.WriteLine($" note: {_note}");
        Console.WriteLine("----------------------------------------------------------------");
    }

    static string ShortGuid(Guid g) => g.ToString()[..8];
}
