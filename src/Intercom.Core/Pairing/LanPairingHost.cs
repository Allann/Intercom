using System.Net;
using Intercom.ControlChannel;
using Intercom.Diagnostics;
using Intercom.Identity;
using Intercom.Chat;
using Intercom.AttentionCards;
using Intercom.Audio;
using Intercom.GroupVoice;

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
    readonly IPeerTransportListener _listener;
    readonly IPeerTransportConnector _connector;
    readonly object _gate = new();
    readonly Dictionary<Guid, ActivePairing> _pairings = [];
    readonly Dictionary<Guid, TlsPairingTransport> _connections = [];
    readonly HashSet<Guid> _connecting = [];
    readonly Dictionary<Guid, Capability> _peerCapabilities = [];
    readonly Capability _localCapabilities;
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

    public IPAddress? ConnectedPeerAddress(Guid peerId)
    {
        lock (_gate) return _connections.TryGetValue(peerId, out var transport) ? transport.RemoteAddress : null;
    }

    public Capability PeerCapabilities(Guid peerId)
    {
        lock (_gate) return _peerCapabilities.GetValueOrDefault(peerId, Capability.Text);
    }

    public LanPairingHost(IdentityStore identityStore, int listenPort, Func<SpkiPin, Guid?> resolvePeerId,
        Capability localCapabilities = Capability.Text | Capability.ChatMarkdown | Capability.ChatImages | Capability.ChatTyping)
        : this(identityStore, resolvePeerId,
            new SslPeerTransportListener(identityStore.Identity.Certificate, listenPort),
            new SslPeerTransportConnector(identityStore.Identity.Certificate), localCapabilities)
    {
    }

    internal LanPairingHost(IdentityStore identityStore, Func<SpkiPin, Guid?> resolvePeerId,
        IPeerTransportListener listener, IPeerTransportConnector connector,
        Capability localCapabilities = Capability.Text | Capability.ChatMarkdown | Capability.ChatImages | Capability.ChatTyping)
    {
        _identityStore = identityStore;
        _resolvePeerId = resolvePeerId;
        _localCapabilities = localCapabilities;
        _listener = listener;
        _connector = connector;
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

    /// <summary>Starts the ordinary pairing ceremony from a manually entered
    /// endpoint. Unlike LAN discovery, the endpoint supplies no identity
    /// claims: peer ID and SPKI are learned from the mutually-authenticated
    /// connection and then verified by the shared SAS ceremony.</summary>
    public async Task ConnectManualAsync(ManualPeerEndpoint endpoint, CancellationToken cancellationToken)
    {
        Guid remotePeerId = Guid.Empty;
        try
        {
            var resolved = await endpoint.ResolveAsync(cancellationToken).ConfigureAwait(false);
            var connection = await _connector.ConnectAsync(resolved, cancellationToken).ConfigureAwait(false);
            var remoteSpki = SpkiHash.Compute(connection.RemoteCertificate);
            var localNonce = PairingNonce.Generate();
            await connection.SendAsync(new PairingNonceMessage
            {
                Nonce = localNonce,
                PeerId = _identityStore.Identity.PeerId,
                ProtocolVersion = Hello.CurrentProtocolVersion,
                Intent = PairingIntent.Pair,
            }.ToFrame(Guid.NewGuid()), cancellationToken).ConfigureAwait(false);

            var firstFrame = await connection.ReceiveAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The peer closed the connection before pairing began.");
            remotePeerId = PairingFrameCodec.DecodeNonce(firstFrame).PeerId;
            await BeginAsync(connection, remotePeerId, remoteSpki, cancellationToken, firstFrame,
                localNonce, sendLocalNonce: false, manualEndpoint: endpoint).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Current.Error("pairing.manual-connect-failed", $"peer={remotePeerId} endpoint={endpoint}", ex);
            PairingFailed?.Invoke(remotePeerId, new InvalidOperationException($"Can’t reach this peer at the saved address ({endpoint}). {ex.Message}", ex));
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
            _identityStore.UpdateLastKnownEndpoint(peer.PeerId, endpoint.Address, endpoint.Port);
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

    public async Task ConnectApprovedAsync(ManualPeerEndpoint endpoint, ApprovedPeer peer, CancellationToken cancellationToken)
    {
        try
        {
            var resolved = await endpoint.ResolveAsync(cancellationToken).ConfigureAwait(false);
            await ConnectApprovedAsync(resolved, peer, cancellationToken).ConfigureAwait(false);
            if (ConnectedPeerIds.Contains(peer.PeerId))
                _identityStore.UpdateLastKnownEndpoint(peer.PeerId, endpoint.Host, endpoint.Port);
            else
                PairingFailed?.Invoke(peer.PeerId,
                    new InvalidOperationException($"Can’t reach this peer at the saved address ({endpoint}). The pairing is still approved; enter a new address and try again."));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Current.Error("peer.manual-reconnect-failed", $"peer={peer.PeerId} endpoint={endpoint}", ex);
            PairingFailed?.Invoke(peer.PeerId, new InvalidOperationException($"Can’t reach this peer at the saved address ({endpoint}). {ex.Message}", ex));
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

    async Task BeginAsync(IPeerTransportConnection connection, Guid remotePeerId, SpkiPin remoteSpki, CancellationToken cancellationToken, ControlFrame? firstFrame = null,
        PairingNonce? localNonce = null, bool sendLocalNonce = true, ManualPeerEndpoint? manualEndpoint = null)
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
            DateTimeOffset.UtcNow,
            localNonce: localNonce);

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
            if (manualEndpoint is not null)
                _identityStore.UpdateLastKnownEndpoint(peer.PeerId, manualEndpoint.Host, manualEndpoint.Port);
            DiagnosticLog.Current.Info("pairing.approved", $"peer={peer.PeerId}");
            PromotePairingConnection(peer.PeerId, transport);
            Approved?.Invoke(peer);
        };
        ceremony.Rejected += () => Remove(remotePeerId);
        ceremony.Expired += () => Remove(remotePeerId);

        transport.Start();
        if (sendLocalNonce) await ceremony.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    internal void PromotePairingConnection(Guid peerId, TlsPairingTransport transport)
    {
        var promoted = TryPromotePairingConnection(peerId, transport);
        CompletePromotion(peerId, transport, promoted);
    }

    bool TryPromotePairingConnection(Guid peerId, TlsPairingTransport transport)
    {
        lock (_gate)
        {
            _pairings.Remove(peerId);
            _connections.TryGetValue(peerId, out var existing);
            if (!PairingConnectionPromotion.ShouldPromote(existing, transport)) return false;
            _connections[peerId] = transport;
            return true;
        }
    }

    void CompletePromotion(Guid peerId, TlsPairingTransport transport, bool promoted)
    {
        if (PairingConnectionPromotion.ShouldDispose(promoted)) { _ = transport.DisposeAsync(); return; }
        WireApprovedTransport(peerId, transport, alreadyStarted: true);
        _ = transport.SendAsync(Hello.Current(_localCapabilities).ToFrame(Guid.NewGuid()), CancellationToken.None);
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
        _ = transport.SendAsync(Hello.Current(_localCapabilities).ToFrame(Guid.NewGuid()), CancellationToken.None);
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
        switch (frame.Type)
        {
            case ControlMessageType.Hello: ReceiveHello(peerId, frame); return;
            case ControlMessageType.Delivered: ReceiveDelivery(peerId, frame); return;
            case ControlMessageType.PairingNonce:
            case ControlMessageType.PairingConfirm:
            case ControlMessageType.PairingReject: return;
            default: ReceiveApplicationFrame(peerId, transport, frame); return;
        }
    }

    void ReceiveHello(Guid peerId, ControlFrame frame)
    {
        try { StoreCapabilities(peerId, HelloFrameCodec.Decode(frame).Capabilities); }
        catch (MalformedFrameException) { }
        ConnectionsChanged?.Invoke();
    }

    void StoreCapabilities(Guid peerId, Capability capabilities)
    {
        lock (_gate) { _peerCapabilities[peerId] = capabilities; }
    }

    void ReceiveDelivery(Guid peerId, ControlFrame frame)
    {
        if (frame.CorrelationId is Guid deliveredFor) DeliveryConfirmed?.Invoke(peerId, deliveredFor);
    }

    void ReceiveApplicationFrame(Guid peerId, TlsPairingTransport transport, ControlFrame frame)
    {
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
            _peerCapabilities.Remove(peerId);
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
    public IAudioControlTransport CreateAudioControlTransport(Guid peerId) => new LanPeerAudioControlTransport(this, peerId);
    public IGroupFloorTransport CreateGroupFloorTransport() => new LanGroupFloorTransport(this);

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

    public async Task<bool> ForgetAsync(Guid peerId)
    {
        TlsPairingTransport? connection;
        ActivePairing? pairing;
        lock (_gate)
        {
            _connections.Remove(peerId, out connection);
            _peerCapabilities.Remove(peerId);
            _pairings.Remove(peerId, out pairing);
            _connecting.Remove(peerId);
        }

        var forgotten = _identityStore.Forget(peerId);
        if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
        if (pairing is not null && !ReferenceEquals(pairing.Transport, connection))
            await pairing.Transport.DisposeAsync().ConfigureAwait(false);
        if (forgotten)
        {
            ConnectionDropped?.Invoke(peerId);
            ConnectionsChanged?.Invoke();
        }
        return forgotten;
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

    internal sealed class TlsPairingTransport(IPeerTransportConnection connection, ControlFrame? firstFrame) : IPairingTransport, IAsyncDisposable
    {
        readonly CancellationTokenSource _cts = new();
        public event Action<ControlFrame>? FrameReceived;
        public event Action? Dropped;
        public IPAddress RemoteAddress => connection.RemoteAddress;

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
    public Capability RemoteCapabilities => _host.PeerCapabilities(_peerId);

    public LanPeerChatTransport(LanPairingHost host, Guid peerId)
    {
        _host = host; _peerId = peerId;
        host.ApplicationFrameReceived += OnFrame;
        host.DeliveryConfirmed += OnDelivered;
        host.ConnectionDropped += OnDropped;
    }
    void OnFrame(Guid peerId, ControlFrame frame)
    {
        LanPeerFrameRouter.ForwardChat(_peerId, peerId, frame, FrameReceived);
    }
    void OnDelivered(Guid peerId, Guid id) => LanPeerFrameRouter.ForwardPeerValue(_peerId, peerId, id, DeliveryConfirmed);
    void OnDropped(Guid peerId) => LanPeerFrameRouter.ForwardPeerSignal(_peerId, peerId, ConnectionDropped);
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
        LanPeerFrameRouter.ForwardAttentionCard(_peerId, peerId, frame, FrameReceived, Acknowledged, Resolved);
    }
    void OnDelivered(Guid peerId, Guid id) => LanPeerFrameRouter.ForwardPeerValue(_peerId, peerId, id, DeliveryConfirmed);
    void OnDropped(Guid peerId) => LanPeerFrameRouter.ForwardPeerSignal(_peerId, peerId, ConnectionDropped);
    public Task SendAsync(ControlFrame frame, CancellationToken cancellationToken) => _host.SendAsync(_peerId, frame, cancellationToken);
}

sealed class LanPeerAudioControlTransport : IAudioControlTransport
{
    readonly LanPairingHost _host;
    readonly Guid _peerId;
    public event Action<ControlFrame>? FrameReceived;
    public event Action? ConnectionDropped;

    public LanPeerAudioControlTransport(LanPairingHost host, Guid peerId)
    {
        _host = host;
        _peerId = peerId;
        host.ApplicationFrameReceived += OnFrame;
        host.ConnectionDropped += OnDropped;
    }

    void OnFrame(Guid peerId, ControlFrame frame)
    {
        LanPeerFrameRouter.ForwardAudio(_peerId, peerId, frame, FrameReceived);
    }

    void OnDropped(Guid peerId) => LanPeerFrameRouter.ForwardPeerSignal(_peerId, peerId, ConnectionDropped);
    public Task SendAsync(ControlFrame frame, CancellationToken cancellationToken) =>
        _host.SendAsync(_peerId, frame, cancellationToken);
}

internal enum AttentionCardFrameRoute { None, Card, Acknowledged, Resolved }

internal static class PairingConnectionPromotion
{
    internal static bool ShouldPromote(object? existing, object candidate) =>
        existing is null || ReferenceEquals(existing, candidate);

    internal static bool ShouldDispose(bool promoted) => !promoted;
}

internal static class LanPeerFrameRouter
{
    internal static void ForwardPeerValue<T>(Guid expectedPeerId, Guid peerId, T value, Action<T>? receive)
    { if (expectedPeerId == peerId) receive?.Invoke(value); }

    internal static void ForwardPeerSignal(Guid expectedPeerId, Guid peerId, Action? receive)
    { if (expectedPeerId == peerId) receive?.Invoke(); }

    internal static void ForwardChat(Guid expectedPeerId, Guid peerId, ControlFrame frame, Action<ControlFrame>? receive)
    { if (IsChatFrame(expectedPeerId, peerId, frame.Type)) receive?.Invoke(frame); }

    internal static void ForwardAudio(Guid expectedPeerId, Guid peerId, ControlFrame frame, Action<ControlFrame>? receive)
    { if (IsAudioFrame(expectedPeerId, peerId, frame.Type)) receive?.Invoke(frame); }

    internal static void ForwardGroupFloor(Guid peerId, ControlFrame frame, Action<Guid, ControlFrame>? receive)
    { if (frame.Type == ControlMessageType.GroupFloor) receive?.Invoke(peerId, frame); }

    internal static void ForwardAttentionCard(Guid expectedPeerId, Guid peerId, ControlFrame frame,
        Action<ControlFrame>? receive, Action<Guid>? acknowledge, Action<Guid>? resolve)
    {
        var route = AttentionCardRoute(expectedPeerId, peerId, frame);
        if (route == AttentionCardFrameRoute.Card) receive?.Invoke(frame);
        else if (route == AttentionCardFrameRoute.Acknowledged) acknowledge?.Invoke(frame.CorrelationId!.Value);
        else if (route == AttentionCardFrameRoute.Resolved) resolve?.Invoke(frame.CorrelationId!.Value);
    }

    internal static bool IsChatFrame(Guid expectedPeerId, Guid peerId, ControlMessageType type) =>
        expectedPeerId == peerId && type is ControlMessageType.Chat or ControlMessageType.ChatTyping
            or ControlMessageType.ChatImageStart or ControlMessageType.ChatImageChunk or ControlMessageType.ChatImageComplete
            or ControlMessageType.ChatImageReceived or ControlMessageType.ChatImageCancelled or ControlMessageType.ChatImageFailed;

    internal static bool IsAudioFrame(Guid expectedPeerId, Guid peerId, ControlMessageType type) =>
        expectedPeerId == peerId && type is ControlMessageType.AudioSessionOffer
            or ControlMessageType.AudioSessionAccepted or ControlMessageType.AudioSessionStopped
            or ControlMessageType.AudioSessionRejected;

    internal static AttentionCardFrameRoute AttentionCardRoute(Guid expectedPeerId, Guid peerId, ControlFrame frame)
    {
        if (expectedPeerId != peerId) return AttentionCardFrameRoute.None;
        return frame.Type switch
        {
            ControlMessageType.AttentionCard => AttentionCardFrameRoute.Card,
            ControlMessageType.Acknowledged when frame.CorrelationId.HasValue => AttentionCardFrameRoute.Acknowledged,
            ControlMessageType.Resolved when frame.CorrelationId.HasValue => AttentionCardFrameRoute.Resolved,
            _ => AttentionCardFrameRoute.None,
        };
    }
}

sealed class LanGroupFloorTransport : IGroupFloorTransport, IDisposable
{
    readonly LanPairingHost _host;

    public LanGroupFloorTransport(LanPairingHost host)
    {
        _host = host;
        _host.ApplicationFrameReceived += OnApplicationFrameReceived;
    }

    public event Action<Guid, ControlFrame>? FrameReceived;

    public async Task SendAsync(IEnumerable<Guid> participantPeerIds, ControlFrame frame, CancellationToken cancellationToken)
    {
        foreach (var peerId in participantPeerIds.Distinct())
            await _host.SendAsync(peerId, frame with { MessageId = Guid.NewGuid(), Payload = frame.Payload.ToArray() }, cancellationToken).ConfigureAwait(false);
    }

    void OnApplicationFrameReceived(Guid peerId, ControlFrame frame)
        => LanPeerFrameRouter.ForwardGroupFloor(peerId, frame, FrameReceived);

    public void Dispose() => _host.ApplicationFrameReceived -= OnApplicationFrameReceived;
}
