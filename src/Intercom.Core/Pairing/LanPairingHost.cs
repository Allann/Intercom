using System.Net;
using Intercom.ControlChannel;
using Intercom.Diagnostics;
using Intercom.Identity;
using Intercom.Chat;
using Intercom.AttentionCards;

namespace Intercom.Pairing;

/// <summary>
/// Owns the real LAN pairing vertical slice: mutual-TLS listening/dialing,
/// certificate-to-discovered-peer routing, nonce exchange, SAS confirmation,
/// and persisted approval. The app shell only supplies a discovered endpoint
/// and reacts to pairing-state events.
/// </summary>
public sealed class LanPairingHost : IAsyncDisposable
{
    readonly IdentityStore _identityStore;
    readonly Func<SpkiPin, Guid?> _resolvePeerId;
    readonly SslPeerTransportListener _listener;
    readonly SslPeerTransportConnector _connector;
    readonly object _gate = new();
    readonly Dictionary<Guid, ActivePairing> _pairings = [];
    readonly Dictionary<Guid, TlsPairingTransport> _connections = [];
    readonly HashSet<Guid> _connecting = [];
    bool _disposed;

    public event Action<Guid, string>? PairingCodeReady;
    public event Action<ApprovedPeer>? Approved;
    public event Action<Guid, Exception>? PairingFailed;
    public event Action? ConnectionsChanged;
    public event Action<Guid, ControlFrame>? ApplicationFrameReceived;
    internal event Action<Guid, Guid>? DeliveryConfirmed;
    internal event Action<Guid>? ConnectionDropped;

    public IReadOnlyList<Guid> ConnectedPeerIds
    {
        get { lock (_gate) { return _connections.Keys.ToList(); } }
    }

    public LanPairingHost(IdentityStore identityStore, int listenPort, Func<SpkiPin, Guid?> resolvePeerId)
    {
        _identityStore = identityStore;
        _resolvePeerId = resolvePeerId;
        _listener = new SslPeerTransportListener(identityStore.Identity.Certificate, listenPort);
        _connector = new SslPeerTransportConnector(identityStore.Identity.Certificate);
        _listener.ConnectionAccepted += OnConnectionAccepted;
    }

    public void Start()
    {
        _listener.Start();
        DiagnosticLog.Current.Info("pairing.listener-started", "Mutual-TLS pairing listener is active.");
    }

    public async Task ConnectAsync(IPEndPoint endpoint, Guid remotePeerId, SpkiPin advertisedSpki, CancellationToken cancellationToken)
    {
        try
        {
            var connection = await _connector.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            var presentedSpki = SpkiHash.Compute(connection.RemoteCertificate);
            if (!SpkiPinConstantTimeComparer.Matches(presentedSpki, advertisedSpki))
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw new InvalidOperationException("The peer certificate does not match its discovery advertisement.");
            }
            await BeginAsync(connection, remotePeerId, presentedSpki, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Current.Error("pairing.connect-failed", $"peer={remotePeerId} endpoint={endpoint}", ex);
            PairingFailed?.Invoke(remotePeerId, ex);
        }
    }

    public async Task ConnectApprovedAsync(IPEndPoint endpoint, ApprovedPeer peer, CancellationToken cancellationToken)
    {
        if (peer.Revoked || peer.SpkiSha256 is not { } expectedSpki) return;
        lock (_gate)
        {
            if (_connections.ContainsKey(peer.PeerId) || !_connecting.Add(peer.PeerId)) return;
        }

        try
        {
            var connection = await _connector.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            var presented = SpkiHash.Compute(connection.RemoteCertificate);
            if (!SpkiPinConstantTimeComparer.Matches(presented, expectedSpki))
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw new InvalidOperationException("The approved peer presented a different identity certificate.");
            }
            EstablishApprovedConnection(peer.PeerId, connection);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Current.Error("peer.reconnect-failed", $"peer={peer.PeerId} endpoint={endpoint}", ex);
        }
        finally
        {
            lock (_gate) { _connecting.Remove(peer.PeerId); }
        }
    }

    void OnConnectionAccepted(IPeerTransportConnection connection) => _ = AcceptAsync(connection);

    async Task AcceptAsync(IPeerTransportConnection connection)
    {
        var spki = SpkiHash.Compute(connection.RemoteCertificate);
        try
        {
            var approved = _identityStore.FindApprovedBySpki(spki);
            if (approved is not null && !approved.Revoked)
            {
                EstablishApprovedConnection(approved.PeerId, connection);
                return;
            }

            var firstFrame = await connection.ReceiveAsync(CancellationToken.None).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Pairing connection closed before identifying itself.");
            var nonce = PairingFrameCodec.DecodeNonce(firstFrame);
            var discoveredPeerId = _resolvePeerId(spki);
            if (discoveredPeerId is Guid expected && expected != nonce.PeerId)
                throw new InvalidOperationException("The peer ID in the pairing request does not match its discovery advertisement.");

            await BeginAsync(connection, nonce.PeerId, spki, CancellationToken.None, firstFrame).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Current.Error("pairing.inbound-failed", $"spki={spki}", ex);
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    async Task BeginAsync(IPeerTransportConnection connection, Guid remotePeerId, SpkiPin remoteSpki, CancellationToken cancellationToken, ControlFrame? firstFrame = null)
    {
        if (!_identityStore.StartPairing(remotePeerId, DateTimeOffset.UtcNow))
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("A pairing request for this peer is already active.");
        }

        var transport = new TlsPairingTransport(connection, firstFrame);
        var ceremony = new PairingCeremonyCoordinator(
            transport,
            _identityStore,
            _identityStore.Identity.SpkiSha256,
            _identityStore.Identity.PeerId,
            remoteSpki,
            remotePeerId,
            connection.RemoteCertificate.RawData,
            DateTimeOffset.UtcNow);

        lock (_gate)
        {
            if (_pairings.ContainsKey(remotePeerId))
            {
                _identityStore.CompletePairing(remotePeerId);
                throw new InvalidOperationException("A live pairing connection already exists for this peer.");
            }
            _pairings[remotePeerId] = new ActivePairing(ceremony, transport);
        }

        ceremony.CodeReady += code =>
        {
            DiagnosticLog.Current.Info("pairing.code-ready", $"peer={remotePeerId}");
            PairingCodeReady?.Invoke(remotePeerId, code);
        };
        ceremony.Approved += peer =>
        {
            DiagnosticLog.Current.Info("pairing.approved", $"peer={peer.PeerId}");
            PromotePairingConnection(peer.PeerId, transport);
            Approved?.Invoke(peer);
        };
        ceremony.Rejected += () => Remove(remotePeerId);
        ceremony.Expired += () => Remove(remotePeerId);

        transport.Start();
        await ceremony.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    void PromotePairingConnection(Guid peerId, TlsPairingTransport transport)
    {
        lock (_gate)
        {
            _pairings.Remove(peerId);
            if (_connections.TryGetValue(peerId, out var existing) && !ReferenceEquals(existing, transport))
            {
                _ = transport.DisposeAsync();
                return;
            }
            _connections[peerId] = transport;
        }
        WireApprovedTransport(peerId, transport, alreadyStarted: true);
    }

    void EstablishApprovedConnection(Guid peerId, IPeerTransportConnection connection)
    {
        var transport = new TlsPairingTransport(connection, null);
        lock (_gate)
        {
            if (_connections.ContainsKey(peerId))
            {
                _ = transport.DisposeAsync();
                return;
            }
            _connections[peerId] = transport;
        }
        WireApprovedTransport(peerId, transport, alreadyStarted: false);
    }

    void WireApprovedTransport(Guid peerId, TlsPairingTransport transport, bool alreadyStarted)
    {
        transport.FrameReceived += frame => OnApprovedFrame(peerId, transport, frame);
        transport.Dropped += () => OnConnectionDropped(peerId, transport);
        if (!alreadyStarted) transport.Start();
        DiagnosticLog.Current.Info("peer.connected", $"peer={peerId}");
        ConnectionsChanged?.Invoke();
    }

    void OnApprovedFrame(Guid peerId, TlsPairingTransport transport, ControlFrame frame)
    {
        if (frame.Type == ControlMessageType.Delivered)
        {
            if (frame.CorrelationId is Guid deliveredFor) DeliveryConfirmed?.Invoke(peerId, deliveredFor);
            return;
        }
        if (frame.Type is ControlMessageType.PairingNonce or ControlMessageType.PairingConfirm or ControlMessageType.PairingReject)
            return;

        ApplicationFrameReceived?.Invoke(peerId, frame);
        _ = transport.SendAsync(new ControlFrame
        {
            Type = ControlMessageType.Delivered,
            MessageId = Guid.NewGuid(),
            CorrelationId = frame.MessageId,
            Payload = [],
        }, CancellationToken.None);
    }

    void OnConnectionDropped(Guid peerId, TlsPairingTransport transport)
    {
        lock (_gate)
        {
            if (!_connections.TryGetValue(peerId, out var current) || !ReferenceEquals(current, transport)) return;
            _connections.Remove(peerId);
        }
        DiagnosticLog.Current.Warning("peer.disconnected", $"peer={peerId}");
        ConnectionDropped?.Invoke(peerId);
        ConnectionsChanged?.Invoke();
    }

    public Task SendAsync(Guid peerId, ControlFrame frame, CancellationToken cancellationToken)
    {
        TlsPairingTransport transport;
        lock (_gate)
        {
            if (!_connections.TryGetValue(peerId, out transport!))
                throw new InvalidOperationException("That family member is offline.");
        }
        return transport.SendAsync(frame, cancellationToken);
    }

    /// <summary>Sends the same application frame to every currently connected
    /// approved peer. Each peer receives its own frame instance/message ID so
    /// Delivered receipts remain unambiguous per connection.</summary>
    public async Task BroadcastAsync(Func<ControlFrame> createFrame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(createFrame);
        Guid[] peers;
        lock (_gate) { peers = _connections.Keys.ToArray(); }

        foreach (var peerId in peers)
        {
            try
            {
                await SendAsync(peerId, createFrame(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // A peer can disconnect between the roster snapshot and send.
                // Presence is a lease and the next successful broadcast repairs it.
            }
        }
    }

    public IChatTransport CreateChatTransport(Guid peerId) => new LanPeerChatTransport(this, peerId);
    public IAttentionCardTransport CreateAttentionCardTransport(Guid peerId) => new LanPeerAttentionCardTransport(this, peerId);

    public Task ConfirmAsync(Guid remotePeerId, string friendlyName, CancellationToken cancellationToken)
    {
        ActivePairing pairing;
        lock (_gate)
        {
            if (!_pairings.TryGetValue(remotePeerId, out pairing!))
                throw new InvalidOperationException("No active pairing request exists for this peer.");
        }
        return pairing.Ceremony.ConfirmLocalAsync(friendlyName, cancellationToken);
    }

    public Task RejectAsync(Guid remotePeerId, CancellationToken cancellationToken)
    {
        ActivePairing pairing;
        lock (_gate)
        {
            if (!_pairings.TryGetValue(remotePeerId, out pairing!)) return Task.CompletedTask;
        }
        return pairing.Ceremony.RejectLocalAsync(cancellationToken);
    }

    void Remove(Guid peerId)
    {
        ActivePairing? pairing;
        lock (_gate) { _pairings.Remove(peerId, out pairing); }
        if (pairing is not null) _ = pairing.Transport.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        List<TlsPairingTransport> transports;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            transports = _pairings.Values.Select(value => value.Transport)
                .Concat(_connections.Values).Distinct().ToList();
            _pairings.Clear();
            _connections.Clear();
            _connecting.Clear();
        }
        _listener.ConnectionAccepted -= OnConnectionAccepted;
        _listener.Dispose();
        foreach (var transport in transports) await transport.DisposeAsync().ConfigureAwait(false);
    }

    sealed record ActivePairing(PairingCeremonyCoordinator Ceremony, TlsPairingTransport Transport);

    sealed class TlsPairingTransport(IPeerTransportConnection connection, ControlFrame? firstFrame) : IPairingTransport, IAsyncDisposable
    {
        readonly CancellationTokenSource _cts = new();
        public event Action<ControlFrame>? FrameReceived;
        public event Action? Dropped;

        public void Start()
        {
            if (firstFrame is not null) FrameReceived?.Invoke(firstFrame);
            _ = ReceiveLoopAsync();
        }
        public Task SendAsync(ControlFrame frame, CancellationToken cancellationToken) => connection.SendAsync(frame, cancellationToken);

        async Task ReceiveLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var frame = await connection.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                    if (frame is null) break;
                    FrameReceived?.Invoke(frame);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                DiagnosticLog.Current.Error("pairing.receive-failed", "TLS pairing receive loop failed.", ex);
            }
            if (!_cts.IsCancellationRequested) Dropped?.Invoke();
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            await connection.DisposeAsync().ConfigureAwait(false);
            _cts.Dispose();
        }
    }
}

sealed class LanPeerChatTransport : IChatTransport
{
    readonly LanPairingHost _host;
    readonly Guid _peerId;
    public event Action<ControlFrame>? FrameReceived;
    public event Action<Guid>? DeliveryConfirmed;
    public event Action? ConnectionDropped;

    public LanPeerChatTransport(LanPairingHost host, Guid peerId)
    {
        _host = host; _peerId = peerId;
        host.ApplicationFrameReceived += OnFrame;
        host.DeliveryConfirmed += OnDelivered;
        host.ConnectionDropped += OnDropped;
    }
    void OnFrame(Guid peerId, ControlFrame frame) { if (peerId == _peerId && frame.Type == ControlMessageType.Chat) FrameReceived?.Invoke(frame); }
    void OnDelivered(Guid peerId, Guid id) { if (peerId == _peerId) DeliveryConfirmed?.Invoke(id); }
    void OnDropped(Guid peerId) { if (peerId == _peerId) ConnectionDropped?.Invoke(); }
    public Task SendAsync(ControlFrame frame, CancellationToken cancellationToken) => _host.SendAsync(_peerId, frame, cancellationToken);
}

sealed class LanPeerAttentionCardTransport : IAttentionCardTransport
{
    readonly LanPairingHost _host;
    readonly Guid _peerId;
    public event Action<ControlFrame>? FrameReceived;
    public event Action<Guid>? DeliveryConfirmed;
    public event Action<Guid>? Acknowledged;
    public event Action<Guid>? Resolved;
    public event Action? ConnectionDropped;

    public LanPeerAttentionCardTransport(LanPairingHost host, Guid peerId)
    {
        _host = host; _peerId = peerId;
        host.ApplicationFrameReceived += OnFrame;
        host.DeliveryConfirmed += OnDelivered;
        host.ConnectionDropped += OnDropped;
    }
    void OnFrame(Guid peerId, ControlFrame frame)
    {
        if (peerId != _peerId) return;
        if (frame.Type == ControlMessageType.AttentionCard) FrameReceived?.Invoke(frame);
        else if (frame.Type == ControlMessageType.Acknowledged && frame.CorrelationId is Guid ack) Acknowledged?.Invoke(ack);
        else if (frame.Type == ControlMessageType.Resolved && frame.CorrelationId is Guid resolved) Resolved?.Invoke(resolved);
    }
    void OnDelivered(Guid peerId, Guid id) { if (peerId == _peerId) DeliveryConfirmed?.Invoke(id); }
    void OnDropped(Guid peerId) { if (peerId == _peerId) ConnectionDropped?.Invoke(); }
    public Task SendAsync(ControlFrame frame, CancellationToken cancellationToken) => _host.SendAsync(_peerId, frame, cancellationToken);
}
