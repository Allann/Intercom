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
// State is modeled per "making illegal states unrepresentable": ConnState is a
// closed hierarchy, one type per state, and data lives only on the state where
// it's meaningful (heartbeat timestamps exist only on Connected/Reconnecting;
// "asleep" is a distinct state that wraps what it interrupted, not a separate
// bool that can drift out of sync with the rest of the state).
//
// Run: dotnet run
// Then type `help` for commands. State prints after every command.

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

// ---- state hierarchy ----
//
// IAwakeState marks every state that sleep can interrupt. Asleep deliberately
// does NOT implement IAwakeState, so Asleep(Asleep(...)) — sleeping while
// already asleep — is a compile error, not a runtime check.

interface IAwakeState { }

abstract record ConnState;

sealed record Idle : ConnState, IAwakeState;                 // no discovery record, no connection
sealed record Discovered : ConnState, IAwakeState;           // mDNS/DNS-SD record seen; NOT trusted, NOT connected
sealed record Connecting : ConnState, IAwakeState;           // TLS handshake + Hello capability exchange in flight
sealed record Connected(int LastHeartbeatAtSecond) : ConnState, IAwakeState;      // heartbeat/lease live
sealed record Reconnecting(int LastHeartbeatAtSecond) : ConnState, IAwakeState;   // lease lapsed or socket dropped, retrying
sealed record Offline : ConnState, IAwakeState;              // lease fully expired, no successful reconnect yet
sealed record Asleep(IAwakeState Was) : ConnState;            // local PC suspended; remembers what it interrupted

sealed class Simulation
{
    // Fixed, illustrative SPKI hashes so the deterministic tie-break has something
    // concrete to compare. Lower hash = initiates as TLS client.
    const string LocalSpki = "3a7f...local";
    const string RemoteSpki = "9c10...remote";

    const int HeartbeatIntervalSeconds = 10; // matches ADR-0001
    const int LeaseSeconds = 30;             // matches ADR-0001

    int _clockSeconds = 0;
    ConnState _state = new Idle();

    Guid _localIncarnation = Guid.NewGuid();
    Guid _remoteIncarnation = Guid.NewGuid();

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
        _note = _state switch
        {
            Idle => Transition(new Discovered(), "Discovery record seen. Peer is VISIBLE, not trusted, not connected."),
            Asleep => Rejected("discover", "the local PC is asleep"),
            _ => $"Already past Discovered ({StateName(_state)}); discovery record re-seen, no state change."
        };
    }

    void ExpireRecord()
    {
        _note = _state switch
        {
            Discovered => Transition(new Idle(), "Discovery record TTL/goodbye expired before we ever connected. Back to Idle."),
            _ => "Discovery record expiry is irrelevant once connected: the heartbeat/lease " +
                 "is the liveness signal now, not the mDNS record (ADR-0001)."
        };
    }

    // ---- connecting ----

    void Connect(bool triggeredByRace)
    {
        _note = _state switch
        {
            Discovered or Reconnecting or Offline =>
                Transition(new Connecting(), triggeredByRace ? "" : "TLS handshake attempt started."),
            Asleep => Rejected("connect", "the local PC is asleep"),
            _ => $"Nothing to connect to from state {StateName(_state)}."
        };
    }

    void Race()
    {
        // Both sides open a connection attempt at ~the same simulated instant.
        // ADR-0001: resolved deterministically by comparing stable peer IDs / SPKI hash.
        // Lower hash initiates as TLS client; the higher hash waits and accepts that
        // connection instead of completing its own outbound attempt.
        Connect(triggeredByRace: true);
        if (_state is not Connecting) return; // Connect() already reported why it couldn't proceed

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
        _note = _state switch
        {
            Connecting => Transition(new Connected(_clockSeconds),
                "Mutual TLS up, one-time Hello capability exchange completed. " +
                "Connected. Heartbeat/lease clock starts now."),
            _ => $"connect-ok is only valid from Connecting (currently {StateName(_state)})."
        };
    }

    void ConnectFail()
    {
        _note = _state switch
        {
            Connecting => Transition(new Reconnecting(_clockSeconds),
                "Handshake failed. Moving to Reconnecting; the deterministic tie-break rule " +
                "applies again on the next attempt (ADR-0001: same rule reused for reconnects)."),
            _ => $"connect-fail is only valid from Connecting (currently {StateName(_state)})."
        };
    }

    // ---- liveness ----

    void Heartbeat(bool stale)
    {
        if (_state is not (Connected or Reconnecting))
        {
            _note = $"Heartbeat received but there's no session to refresh (state {StateName(_state)}). Ignored.";
            return;
        }

        if (stale)
        {
            _note = "Heartbeat carried an OLD incarnation/sequence. REJECTED: a changed incarnation " +
                     "resets the sequence, and a late packet from a superseded incarnation is never " +
                     "accepted as current (ADR-0001 / presence research). Lease NOT refreshed.";
            return;
        }

        _note = _state switch
        {
            Reconnecting => Transition(new Connected(_clockSeconds),
                "Fresh heartbeat arrived while reconnecting -> treated as reconnection success. Connected again."),
            Connected => Transition(new Connected(_clockSeconds),
                "Fresh in-order heartbeat. Lease refreshed (this same signal doubles as connection liveness)."),
            _ => _note
        };
    }

    void Tick(int seconds)
    {
        _clockSeconds += seconds;

        if (_state is Connected connected)
        {
            var age = _clockSeconds - connected.LastHeartbeatAtSecond;
            if (age > LeaseSeconds)
            {
                _note = Transition(new Reconnecting(connected.LastHeartbeatAtSecond),
                    $"{age}s since last heartbeat > {LeaseSeconds}s lease. Lease expired: " +
                    "this doubles as \"connection is dead,\" not just \"presence is stale\" " +
                    "(ADR-0001). Moving to Reconnecting.");
                return;
            }
        }
        else if (_state is Reconnecting reconnecting)
        {
            var age = _clockSeconds - reconnecting.LastHeartbeatAtSecond;
            if (age > LeaseSeconds * 3) // no successful reconnect for a while -> give up to Offline
            {
                _note = Transition(new Offline(), "No successful reconnect for an extended period. Offline.");
                return;
            }
        }

        _note = $"Clock advanced by {seconds}s (now t={_clockSeconds}s).";
    }

    // ---- disruptions ----

    void Drop()
    {
        _note = _state switch
        {
            Connected connected => Transition(new Reconnecting(connected.LastHeartbeatAtSecond),
                "TCP connection dropped (network blip). Any in-flight unacknowledged message is " +
                "now FAILED — no auto-resend across this reconnect (ADR-0001). Moving to Reconnecting; " +
                "both sides will retry using the same deterministic tie-break as initial connect."),
            _ => $"Nothing connected to drop (state {StateName(_state)})."
        };
    }

    void Sleep()
    {
        // Asleep wraps whatever awake state it interrupted. Because Asleep does NOT
        // implement IAwakeState, this is the only place that type-checks, and no
        // other transition can accidentally leave us "Connected AND asleep" — you
        // are either awake in some state, or Asleep; never both at once.
        _note = _state switch
        {
            IAwakeState awake => Transition(new Asleep(awake),
                awake is Connected
                    ? "Local PC suspending (PBT_APMSUSPEND). Best-effort 'about to be unavailable' is " +
                      "advertised but not guaranteed delivered; moving to Asleep rather than staying Connected."
                    : "Local PC suspending; nothing meaningfully connected to advertise."),
            Asleep => "Already asleep; no state change.",
            // Every ConnState is either IAwakeState or Asleep — this branch is
            // unreachable unless a future state is added to the hierarchy without
            // implementing IAwakeState or handling it explicitly here.
            _ => throw new InvalidOperationException($"Unhandled ConnState: {_state.GetType().Name}")
        };
    }

    void Resume()
    {
        if (_state is not Asleep)
        {
            _note = "Not asleep; resume is a no-op.";
            return;
        }
        // Per presence research: resume does NOT jump straight back to whatever it
        // was before sleeping. It must re-establish sockets/discovery and re-evaluate
        // first — so we deliberately discard the wrapped `Was` state rather than restore it.
        _note = Transition(new Discovered(),
            "Local PC resumed (PBT_APMRESUMEAUTOMATIC / user resume). Do NOT advertise available " +
            "immediately — re-discover and reconnect from scratch. Back to Discovered; issue " +
            "'connect' to re-establish.");
    }

    void WifiChange()
    {
        _note = _state switch
        {
            Asleep => Rejected("wifi-change", "the local PC is asleep"),
            Connected => Transition(new Discovered(),
                "Network adapter/address changed. Discovery must re-register/re-browse on the new " +
                "interface. The approved-peer identity (SPKI pin) is UNCHANGED by this — only the " +
                "reachability path is invalidated, not trust. Back to Discovered."),
            _ => Transition(new Discovered(), "Network adapter/address changed; re-evaluating from Discovered.")
        };
    }

    void Restart()
    {
        _localIncarnation = Guid.NewGuid();
        _note = Transition(new Idle(),
            $"Local process restarted. New local incarnation ({ShortGuid(_localIncarnation)}) " +
            "supersedes the old one; nothing from the previous run can be accepted as current. Back to Idle.");
    }

    void RemoteRestart()
    {
        _remoteIncarnation = Guid.NewGuid();
        _note = _state switch
        {
            Connected => Transition(new Reconnecting(_clockSeconds),
                $"Remote peer restarted (new remote incarnation {ShortGuid(_remoteIncarnation)}). " +
                "Its sequence counter resets; any late packet claiming the OLD incarnation must be " +
                "rejected (see 'heartbeat-stale'). Moving to Reconnecting to re-establish under the new incarnation."),
            _ => "Remote peer restarted; not currently connected to it anyway."
        };
    }

    // ---- helpers ----

    string Transition(ConnState next, string note)
    {
        _state = next;
        return note;
    }

    static string Rejected(string command, string reason) => $"'{command}' ignored: {reason}.";

    static string StateName(ConnState s) => s switch
    {
        Asleep a => $"Asleep(was {StateName((ConnState)a.Was)})",
        _ => s.GetType().Name
    };

    // ---- output ----

    public void PrintState()
    {
        Console.WriteLine("----------------------------------------------------------------");
        Console.WriteLine($" t={_clockSeconds}s | state={StateName(_state)}");
        Console.WriteLine($" local incarnation={ShortGuid(_localIncarnation)}  remote incarnation={ShortGuid(_remoteIncarnation)}");
        Console.WriteLine($" local SPKI={LocalSpki}  remote SPKI={RemoteSpki}  (lower wins tie-break)");

        var heartbeatState = _state switch
        {
            Connected c => c.LastHeartbeatAtSecond.ToString(),
            Reconnecting r => r.LastHeartbeatAtSecond.ToString(),
            _ => "n/a for this state"
        };
        Console.WriteLine($" last heartbeat at t={heartbeatState}  lease={LeaseSeconds}s  heartbeat-interval={HeartbeatIntervalSeconds}s");

        if (_note.Length > 0) Console.WriteLine($" note: {_note}");
        Console.WriteLine("----------------------------------------------------------------");
    }

    static string ShortGuid(Guid g) => g.ToString()[..8];
}
