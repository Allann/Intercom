using System.Net;
using Intercom.Identity;
using Intercom.Presence;

namespace Intercom.ControlChannel;

/// <summary>
/// Owns one peer's control-channel connection lifecycle end to end: the
/// <see cref="ConnState"/> machine (issue #14 prototype fidelity), the
/// deterministic SPKI tie-break for who dials whom (both the initial connect
/// and every post-drop reconnect), mutual TLS via the injected transport
/// seam, the one-time Hello exchange, fail-closed SPKI validation, the
/// unified heartbeat/lease (which doubles as connection liveness — no
/// separate keepalive), and generic framed-message send/receive with
/// mechanical Delivered receipts.
///
/// Composition mirrors <c>Intercom.Discovery.DiscoveryService</c>: policy
/// (<see cref="PeerConnectionStateMachine"/>, <see cref="PeerCertificateValidator"/>,
/// <see cref="FrameDispatcher"/>, <see cref="TieBreak"/>) is plain, fully
/// tested C#; native/OS-facing I/O is hidden behind
/// <see cref="IPeerTransportConnector"/>/<see cref="IPeerTransportConnection"/>
/// so this class itself can be driven by hand-written fakes in tests, the
/// same way <c>DiscoveryServiceTests</c> drives <c>DiscoveryService</c> with
/// <c>FakeDnsServiceDiscovery</c>.
///
/// One instance manages exactly one peer's logical channel. A shared
/// <see cref="IPeerTransportListener"/> (one per app, one TCP port) is NOT
/// owned here — routing "which peer does this freshly-accepted inbound
/// connection belong to" across multiple PeerControlChannel instances is
/// app-shell connection-routing wiring, deliberately left out of this
/// ticket's scope (see the #21 report); <see cref="AcceptInboundAsync"/> is
/// what such a router would call once it has made that determination.
///
/// Issue #23 adds one more per-connection periodic concern on top of the
/// existing heartbeat/lease: a presence broadcast, reusing this same
/// <see cref="Tick"/>-driven cadence rather than a second parallel timer.
/// When constructed with a non-null <paramref name="presenceLeaseProvider"/>
/// (see the constructor), every <see cref="Tick"/> call also checks whether
/// a fresh presence frame is due for the current connection and, if so,
/// best-effort sends one — but ONLY while <see cref="Trust"/> is
/// <see cref="ConnectionTrust.Approved"/> (docs/research/active-device-
/// presence.md's hard privacy rule: presence must never be sent during
/// pairing or before a connection is authenticated).
/// </summary>
public sealed class PeerControlChannel : IAsyncDisposable
{
    // Presence jitter is not security-sensitive (unlike issue #22's pairing
    // nonces) — an ordinary PRNG is the appropriate, simpler choice here.
    static readonly Random PresenceJitterRandom = new();

    readonly PeerConnectionStateMachine _stateMachine = new();
    readonly IPeerTransportConnector _connector;
    readonly SpkiPin _localSpki;
    readonly Capability _localCapabilities;
    readonly Func<IReadOnlyList<ApprovedPeer>> _approvedPeers;
    readonly ApprovedPeer? _expectedApprovedPeer;
    readonly Func<DateTimeOffset> _clock;
    readonly Func<PresenceLease>? _presenceLeaseProvider;

    // Guards the fields below and serializes "am I already mid-handshake"
    // decisions between EvaluateConnectAsync and AcceptInboundAsync, which
    // can legitimately race (e.g. discovery re-evaluates and re-triggers a
    // dial attempt while an inbound connection from the same peer is being
    // accepted). Never held across an await — every await happens either
    // before the lock is taken or after it's released.
    readonly object _gate = new();
    IPeerTransportConnection? _connection;
    FrameDispatcher? _dispatcher;
    ConnectionTrust? _trust;
    CancellationTokenSource? _receiveLoopCts;
    bool _handshakeInFlight;
    bool _disposed;

    // Presence-send scheduling state, guarded by _gate alongside the
    // connection fields above — a presence send must never race a
    // connection being torn down/replaced.
    DateTimeOffset _nextPresenceSendAt = DateTimeOffset.MinValue;
    bool _forcePresenceSend;

    public ConnState State => _stateMachine.State;

    public ConnectionTrust? Trust
    {
        get { lock (_gate) { return _trust; } }
    }

    /// <summary>Raised on every ConnState transition (never for a no-op).</summary>
    public event Action<ConnState, ConnState>? StateChanged
    {
        add => _stateMachine.StateChanged += value;
        remove => _stateMachine.StateChanged -= value;
    }

    /// <summary>Raised for every accepted inbound application frame (i.e.
    /// past Hello-ordering and trust-mode checks) — Hello and Delivered
    /// receipts are handled internally and not re-surfaced here.</summary>
    public event Action<ControlFrame>? MessageReceived;

    /// <summary>Raised when a connection expected to reach a specific,
    /// previously-approved peer instead presented a certificate with a
    /// different SPKI hash (ADR-0002's "identity changed", never a silent
    /// re-pair).</summary>
    public event Action<PeerIdentityOutcome.IdentityChanged>? IdentityChanged;

    public PeerControlChannel(
        IPeerTransportConnector connector,
        SpkiPin localSpki,
        Capability localCapabilities,
        Func<IReadOnlyList<ApprovedPeer>> approvedPeers,
        ApprovedPeer? expectedApprovedPeer = null,
        Func<DateTimeOffset>? clock = null,
        Func<PresenceLease>? presenceLeaseProvider = null)
    {
        _connector = connector;
        _localSpki = localSpki;
        _localCapabilities = localCapabilities;
        _approvedPeers = approvedPeers;
        _expectedApprovedPeer = expectedApprovedPeer;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _presenceLeaseProvider = presenceLeaseProvider;
    }

    /// <summary>The peer became visible via discovery (issue #20). Does not
    /// connect by itself — see <see cref="EvaluateConnectAsync"/>.</summary>
    public void OnDiscovered() => _stateMachine.Discover();

    public void OnDiscoveryExpired() => _stateMachine.ExpireRecord();

    public void OnSleep() => _stateMachine.Sleep();
    public void OnResume() => _stateMachine.Resume();
    public void OnWifiChange() => _stateMachine.WifiChange();

    /// <summary>Advances the heartbeat/lease clock, and (issue #23) checks
    /// whether a fresh presence frame is due. Callers drive this from a
    /// periodic timer (e.g. every second) — this class never reads the wall
    /// clock itself for lease evaluation, mirroring
    /// <c>VisiblePeerList.EvaluateExpiry</c>'s explicit-clock pattern.</summary>
    public void Tick(DateTimeOffset now)
    {
        _stateMachine.Tick(now);
        TrySendPresenceIfDue(now);
    }

    /// <summary>Requests an out-of-cadence presence send on the next
    /// <see cref="Tick"/> — for a meaningful state change
    /// (docs/research/active-device-presence.md: "immediate update on
    /// meaningful state changes"), rather than waiting up to the full
    /// jittered heartbeat interval. A no-op if no presence provider was
    /// configured; harmless to call when not currently Approved — the next
    /// Tick after a fresh connection becomes Approved already sends
    /// immediately on its own (see <see cref="CompleteHandshakeAsync"/>).</summary>
    public void NotifyPresenceChanged()
    {
        lock (_gate) { _forcePresenceSend = true; }
    }

    /// <summary>docs/research/active-device-presence.md: "heartbeat every 10
    /// seconds"; reuses <see cref="PeerConnectionStateMachine.HeartbeatInterval"/>
    /// (the SAME constant driving connection-liveness heartbeats) rather
    /// than inventing a second cadence, with a small random jitter so peers
    /// don't send in lockstep.</summary>
    static TimeSpan JitteredPresenceInterval() =>
        PeerConnectionStateMachine.HeartbeatInterval + TimeSpan.FromMilliseconds(PresenceJitterRandom.Next(-1000, 1000));

    void TrySendPresenceIfDue(DateTimeOffset now)
    {
        if (_presenceLeaseProvider is null) return;

        IPeerTransportConnection connection;
        lock (_gate)
        {
            // Hard privacy rule (docs/research/active-device-presence.md):
            // presence is sent ONLY over an already-Approved connection —
            // never during pairing (PairingOnly), never before a connection
            // exists.
            if (_trust is not ConnectionTrust.Approved || _connection is null) return;

            var due = _forcePresenceSend || now >= _nextPresenceSendAt;
            if (!due) return;

            connection = _connection;
            _forcePresenceSend = false;
            _nextPresenceSendAt = now + JitteredPresenceInterval();
        }

        var lease = _presenceLeaseProvider();
        _ = TrySendAsync(connection, lease.ToFrame(Guid.NewGuid()));
    }

    /// <summary>Applies the deterministic tie-break and dials out if — and
    /// only if — this device is the initiator for <paramref name="remoteSpki"/>.
    /// If the tie-break says to wait, this is a no-op: the higher-hash side
    /// never dials, it only ever accepts (via <see cref="AcceptInboundAsync"/>).
    /// Safe to call repeatedly (e.g. from discovery re-evaluation or a
    /// reconnect retry loop) — a call that finds a handshake already in
    /// flight, or the state machine not in a connectable state, is a no-op.</summary>
    public async Task EvaluateConnectAsync(IPEndPoint remoteEndpoint, SpkiPin remoteSpki, CancellationToken cancellationToken)
    {
        if (TieBreak.Resolve(_localSpki, remoteSpki) != TieBreakRole.InitiateAsClient) return;
        if (!TryBeginHandshake()) return;

        _stateMachine.Connect();
        if (_stateMachine.State is not ConnState.Connecting)
        {
            EndHandshake();
            return;
        }

        try
        {
            var connection = await _connector.ConnectAsync(remoteEndpoint, cancellationToken).ConfigureAwait(false);
            await CompleteHandshakeAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _stateMachine.ConnectFail(_clock());
        }
        finally
        {
            EndHandshake();
        }
    }

    /// <summary>Called by a connection router (see the class doc) once it
    /// has determined an inbound, already-TLS-authenticated connection
    /// belongs to this peer. If a handshake is already in flight, or the
    /// state machine isn't in a connectable state, the inbound connection is
    /// disposed rather than displacing whatever is already happening — this
    /// is what keeps "exactly one logical channel" true even if both sides
    /// somehow dial at once despite the tie-break.</summary>
    public async Task AcceptInboundAsync(IPeerTransportConnection connection, CancellationToken cancellationToken)
    {
        if (!TryBeginHandshake())
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            return;
        }

        _stateMachine.Connect();
        if (_stateMachine.State is not ConnState.Connecting)
        {
            EndHandshake();
            await connection.DisposeAsync().ConfigureAwait(false);
            return;
        }

        try
        {
            await CompleteHandshakeAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            EndHandshake();
        }
    }

    bool TryBeginHandshake()
    {
        lock (_gate)
        {
            if (_handshakeInFlight || _disposed) return false;
            _handshakeInFlight = true;
            return true;
        }
    }

    void EndHandshake()
    {
        lock (_gate) { _handshakeInFlight = false; }
    }

    async Task CompleteHandshakeAsync(IPeerTransportConnection connection, CancellationToken cancellationToken)
    {
        var outcome = PeerCertificateValidator.Validate(connection.RemoteCertificate, _expectedApprovedPeer, _approvedPeers());

        if (outcome is PeerIdentityOutcome.IdentityChanged changed)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            _stateMachine.ConnectFail(_clock());
            IdentityChanged?.Invoke(changed);
            return;
        }

        ConnectionTrust trust = outcome switch
        {
            PeerIdentityOutcome.Approved a => new ConnectionTrust.Approved { PeerId = a.Peer.PeerId },
            PeerIdentityOutcome.PairingOnly => new ConnectionTrust.PairingOnly(),
            _ => throw new InvalidOperationException($"Unhandled {nameof(PeerIdentityOutcome)}: {outcome.GetType().Name}"),
        };

        var dispatcher = new FrameDispatcher(trust);
        dispatcher.FrameAccepted += frame =>
        {
            if (frame.Type is not (ControlMessageType.Hello or ControlMessageType.Delivered))
            {
                MessageReceived?.Invoke(frame);
            }
        };
        dispatcher.DeliveredReceiptReady += receipt => _ = TrySendAsync(connection, receipt);

        try
        {
            var helloMessageId = Guid.NewGuid();
            await connection.SendAsync(Hello.Current(_localCapabilities).ToFrame(helloMessageId), cancellationToken).ConfigureAwait(false);

            var helloReply = await connection.ReceiveAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new MalformedFrameException("Connection closed before a Hello reply arrived.");
            if (!dispatcher.Dispatch(helloReply))
            {
                throw new MalformedFrameException("Expected Hello as the first frame from the peer.");
            }
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            _stateMachine.ConnectFail(_clock());
            return;
        }

        var incarnation = Guid.NewGuid();
        var receiveLoopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        lock (_gate)
        {
            if (_disposed)
            {
                receiveLoopCts.Cancel();
                receiveLoopCts.Dispose();
                _ = connection.DisposeAsync();
                return;
            }

            _connection = connection;
            _dispatcher = dispatcher;
            _trust = trust;
            _receiveLoopCts = receiveLoopCts;

            // A fresh connection is itself a meaningful change — send
            // presence on the very next Tick rather than waiting up to a
            // full jittered heartbeat interval (no-op if this connection
            // isn't Approved or no provider was configured; TrySendPresenceIfDue
            // re-checks both).
            _forcePresenceSend = true;
        }

        _stateMachine.ConnectOk(incarnation, _clock());
        _ = ReceiveLoopAsync(connection, dispatcher, incarnation, receiveLoopCts.Token);
    }

    async Task ReceiveLoopAsync(IPeerTransportConnection connection, FrameDispatcher dispatcher, Guid incarnation, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await connection.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (frame is null) break; // peer closed cleanly

                if (!dispatcher.Dispatch(frame))
                {
                    // A well-behaved peer never sends a frame we'll reject
                    // (wrong ordering, or disallowed under PairingOnly) —
                    // treat this the same as a transport failure.
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on teardown (Dispose / a fresh handshake superseding
            // this one) — not a drop to react to.
            return;
        }
        catch
        {
            // Any other exception (malformed frame, IOException, etc.) means
            // the connection is dead — fall through to Drop() below.
        }

        if (cancellationToken.IsCancellationRequested) return;

        lock (_gate)
        {
            // Only react if this receive loop's connection is still the
            // current one — a loop belonging to a superseded incarnation
            // must never affect the state a newer connection already
            // established (this is the real-world analogue of the
            // prototype's stale-heartbeat rejection: an old receive loop
            // racing a brand new connection).
            if (!ReferenceEquals(_connection, connection)) return;

            _connection = null;
            _dispatcher = null;
            _trust = null;
            _receiveLoopCts = null;
        }

        _stateMachine.Drop();
    }

    Task TrySendAsync(IPeerTransportConnection connection, ControlFrame frame)
    {
        return connection.SendAsync(frame, CancellationToken.None);
        // Best-effort: if this fails, the receive loop's own next read will
        // observe the same dead connection and drive Drop() — a failed
        // Delivered receipt doesn't need its own separate error path.
    }

    /// <summary>Sends one application frame over the current connection.
    /// Throws <see cref="InvalidOperationException"/> if not currently
    /// connected — callers are responsible for manual retry after a drop
    /// (ADR-0001: no auto-resend across a dropped connection).</summary>
    public Task SendAsync(ControlFrame frame, CancellationToken cancellationToken)
    {
        IPeerTransportConnection connection;
        lock (_gate)
        {
            if (_connection is null) throw new InvalidOperationException("Not connected.");
            connection = _connection;
        }
        return connection.SendAsync(frame, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        IPeerTransportConnection? connection;
        CancellationTokenSource? receiveLoopCts;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            connection = _connection;
            receiveLoopCts = _receiveLoopCts;
            _connection = null;
            _dispatcher = null;
            _trust = null;
            _receiveLoopCts = null;
        }

        receiveLoopCts?.Cancel();
        receiveLoopCts?.Dispose();
        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
