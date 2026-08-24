using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Intercom.Identity;
using Intercom.Pairing;
using Intercom.Chat;
using Intercom.AttentionCards;
using Intercom.ControlChannel;
using Intercom.Presence;
using Xunit;

namespace Intercom.App.Tests.Pairing;

public sealed class LanPairingHostTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "IntercomLanPairingTests_" + Guid.NewGuid());

    [Fact(Skip = "Windows LAN integration QA: requires an interactive supported-Windows host with live loopback TLS.")]
    [Trait("Category", "WindowsLanIntegration")]
    public async Task ManualHostname_UsesSamePairingCeremony_AndPersistsHostname()
    {
        var aStore = Store("manual-a");
        var bStore = Store("manual-b");
        var port = FreePort();
        await using var a = new LanPairingHost(aStore, port, _ => null);
        await using var b = new LanPairingHost(bStore, port + 1, _ => null);
        var aCode = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bCode = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        a.PairingCodeReady += (_, code) => aCode.TrySetResult(code);
        b.PairingCodeReady += (_, code) => bCode.TrySetResult(code);
        a.Start();
        b.Start();

        await a.ConnectManualAsync(new ManualPeerEndpoint("localhost", port + 1), CancellationToken.None);
        Assert.Equal(await aCode.Task.WaitAsync(TimeSpan.FromSeconds(5)), await bCode.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await a.ConfirmAsync(bStore.Identity.PeerId, "VPN PC", CancellationToken.None);
        await b.ConfirmAsync(aStore.Identity.PeerId, "PC A", CancellationToken.None);
        await WaitUntilAsync(() => aStore.ApprovedPeers.SingleOrDefault()?.LastKnownAddress == "localhost");

        Assert.Equal("localhost", aStore.ApprovedPeers[0].LastKnownAddress);
        Assert.Equal(port + 1, aStore.ApprovedPeers[0].LastKnownPort);
        Assert.Equal(bStore.Identity.SpkiSha256, aStore.ApprovedPeers[0].SpkiSha256);
    }

    [Fact(Skip = "Windows LAN integration QA: requires an interactive supported-Windows host with live loopback TLS.")]
    [Trait("Category", "WindowsLanIntegration")]
    public async Task TwoRealTlsHosts_ShowSameCode_AndBothPersistApproval()
    {
        var aStore = Store("a");
        var bStore = Store("b");
        var port = FreePort();

        await using var a = new LanPairingHost(aStore, port, pin =>
            pin == bStore.Identity.SpkiSha256 ? bStore.Identity.PeerId : null);
        await using var b = new LanPairingHost(bStore, port + 1, pin =>
            pin == aStore.Identity.SpkiSha256 ? aStore.Identity.PeerId : null);

        var aCode = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bCode = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        a.PairingCodeReady += (_, code) => aCode.TrySetResult(code);
        b.PairingCodeReady += (_, code) => bCode.TrySetResult(code);
        a.Start();
        b.Start();

        await a.ConnectAsync(
            new IPEndPoint(IPAddress.Loopback, port + 1),
            bStore.Identity.PeerId,
            bStore.Identity.SpkiSha256,
            CancellationToken.None);

        Assert.Equal(await aCode.Task.WaitAsync(TimeSpan.FromSeconds(5)),
            await bCode.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        await a.ConfirmAsync(bStore.Identity.PeerId, "PC B", CancellationToken.None);
        await b.ConfirmAsync(aStore.Identity.PeerId, "PC A", CancellationToken.None);

        await WaitUntilAsync(() => aStore.ApprovedPeers.Count == 1 && bStore.ApprovedPeers.Count == 1);
        Assert.Equal(bStore.Identity.SpkiSha256, aStore.ApprovedPeers[0].SpkiSha256);
        Assert.Equal(aStore.Identity.SpkiSha256, bStore.ApprovedPeers[0].SpkiSha256);
        Assert.Equal("PC B", aStore.ApprovedPeers[0].FriendlyName);
        Assert.Equal("PC A", bStore.ApprovedPeers[0].FriendlyName);
    }

    [Fact(Skip = "Windows LAN integration QA: requires an interactive supported-Windows host with live loopback TLS.")]
    [Trait("Category", "WindowsLanIntegration")]
    public async Task ApprovedPeers_ReconnectAfterRestart_AndExchangeRealChat()
    {
        var aInitial = Store("restart-a");
        var bInitial = Store("restart-b");
        aInitial.Approve(Peer(bInitial, "PC B"));
        bInitial.Approve(Peer(aInitial, "PC A"));
        var aStore = Reload("restart-a");
        var bStore = Reload("restart-b");
        var port = FreePort();

        await using var a = new LanPairingHost(aStore, port, _ => null);
        await using var b = new LanPairingHost(bStore, port + 1, _ => null);
        a.Start();
        b.Start();

        await a.ConnectApprovedAsync(
            new IPEndPoint(IPAddress.Loopback, port + 1),
            aStore.ApprovedPeers[0],
            CancellationToken.None);
        await WaitUntilAsync(() => a.ConnectedPeerIds.Contains(bStore.Identity.PeerId)
            && b.ConnectedPeerIds.Contains(aStore.Identity.PeerId));

        var aChat = new ChatService(a.CreateChatTransport(bStore.Identity.PeerId));
        var bChat = new ChatService(b.CreateChatTransport(aStore.Identity.PeerId));
        var received = new TaskCompletionSource<ChatMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        bChat.MessageReceived += message => received.TrySetResult(message);

        var sent = await aChat.SendAsync("hello from A", CancellationToken.None);
        Assert.Equal("hello from A", (await received.Task.WaitAsync(TimeSpan.FromSeconds(5))).Text);
        await WaitUntilAsync(() => aChat.Conversation.Messages
            .Single(message => message.MessageId == sent.MessageId).DeliveryState == ChatDeliveryState.Delivered);

        var aCards = new AttentionCardService(a.CreateAttentionCardTransport(bStore.Identity.PeerId));
        var bCards = new AttentionCardService(b.CreateAttentionCardTransport(aStore.Identity.PeerId));
        var cardReceived = new TaskCompletionSource<AttentionCard>(TaskCreationOptions.RunContinuationsAsynchronously);
        bCards.CardReceived += card => cardReceived.TrySetResult(card);
        var sentCard = await aCards.SendAsync("Dinner's ready", "🍽️", CancellationToken.None);
        var receivedCard = await cardReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Dinner's ready", receivedCard.Purpose);
        await bCards.AcknowledgeAsync(receivedCard.MessageId, CancellationToken.None);
        await WaitUntilAsync(() => aCards.Conversation.Cards
            .Single(card => card.MessageId == sentCard.MessageId).AckState == AttentionCardAckState.Acknowledged);
    }

    [Fact(Skip = "Windows LAN integration QA: requires an interactive supported-Windows host with live loopback TLS.")]
    [Trait("Category", "WindowsLanIntegration")]
    public async Task ApprovedPeers_BroadcastPresenceToEveryConnectedPeer()
    {
        var aStore = Store("presence-a");
        var bStore = Store("presence-b");
        aStore.Approve(Peer(bStore, "PC B"));
        bStore.Approve(Peer(aStore, "PC A"));
        var port = FreePort();

        await using var a = new LanPairingHost(aStore, port, _ => null);
        await using var b = new LanPairingHost(bStore, port + 1, _ => null);
        a.Start();
        b.Start();

        await a.ConnectApprovedAsync(
            new IPEndPoint(IPAddress.Loopback, port + 1),
            aStore.ApprovedPeers[0],
            CancellationToken.None);
        await WaitUntilAsync(() => a.ConnectedPeerIds.Contains(bStore.Identity.PeerId));

        var received = new TaskCompletionSource<PresenceLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.ApplicationFrameReceived += (_, frame) =>
        {
            if (frame.Type == ControlMessageType.Presence)
                received.TrySetResult(PresenceFrameCodec.Decode(frame));
        };

        await a.BroadcastAsync(() => new PresenceLease
        {
            DeviceId = aStore.Identity.PeerId,
            ContactId = aStore.Identity.PeerId,
            IncarnationId = Guid.NewGuid(),
            Sequence = 1,
            Availability = AvailabilityState.Available,
            Dnd = false,
            IdleAgeBucket = IdleAgeBucket.UnderTwoMinutes,
            LeaseSeconds = 30,
            Capabilities = Capability.Text | Capability.AttentionCards,
        }.ToFrame(Guid.NewGuid()), CancellationToken.None);

        var lease = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(aStore.Identity.PeerId, lease.DeviceId);
        Assert.Equal(Capability.Text | Capability.AttentionCards, lease.Capabilities);
    }

    [Fact(Skip = "Windows LAN integration QA: requires an interactive supported-Windows host with live loopback TLS.")]
    [Trait("Category", "WindowsLanIntegration")]
    public async Task ForgetAsync_RevokesTrustAndDisconnectsTheLivePeer()
    {
        var aStore = Store("forget-a");
        var bStore = Store("forget-b");
        aStore.Approve(Peer(bStore, "PC B"));
        bStore.Approve(Peer(aStore, "PC A"));
        var port = FreePort();
        await using var a = new LanPairingHost(aStore, port, _ => null);
        await using var b = new LanPairingHost(bStore, port + 1, _ => null);
        a.Start();
        b.Start();
        await a.ConnectApprovedAsync(new IPEndPoint(IPAddress.Loopback, port + 1), aStore.ApprovedPeers[0], CancellationToken.None);
        await WaitUntilAsync(() => a.ConnectedPeerIds.Contains(bStore.Identity.PeerId));

        Assert.True(await a.ForgetAsync(bStore.Identity.PeerId));

        Assert.DoesNotContain(bStore.Identity.PeerId, a.ConnectedPeerIds);
        Assert.True(aStore.ApprovedPeers.Single(peer => peer.PeerId == bStore.Identity.PeerId).Revoked);
    }

    IdentityStore Store(string name)
    {
        var store = new IdentityStore(Path.Combine(_root, name));
        store.LoadOrCreate();
        return store;
    }

    IdentityStore Reload(string name)
    {
        var store = new IdentityStore(Path.Combine(_root, name));
        store.LoadOrCreate();
        return store;
    }

    static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(condition());
    }

    static ApprovedPeer Peer(IdentityStore remote, string name) => new()
    {
        PeerId = remote.Identity.PeerId,
        FriendlyName = name,
        SpkiSha256 = remote.Identity.SpkiSha256,
        Certificate = remote.Identity.Certificate.RawData,
        ApprovedAt = DateTimeOffset.UtcNow,
    };

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    sealed class FakeListener : IPeerTransportListener
    {
        public bool Disposed { get; private set; }
        public event Action<IPeerTransportConnection>? ConnectionAccepted;
        public void Start() { }
        public void Dispose() => Disposed = true;
        public void Accept(IPeerTransportConnection connection) => ConnectionAccepted?.Invoke(connection);
    }

    sealed class FakeConnector(IPeerTransportConnection connection) : IPeerTransportConnector
    {
        public Task<IPeerTransportConnection> ConnectAsync(IPEndPoint endpoint, CancellationToken cancellationToken) =>
            Task.FromResult(connection);
    }

    sealed class QueueConnector(params IPeerTransportConnection[] connections) : IPeerTransportConnector
    {
        readonly Queue<IPeerTransportConnection> _connections = new(connections);
        public Task<IPeerTransportConnection> ConnectAsync(IPEndPoint endpoint, CancellationToken cancellationToken) =>
            Task.FromResult(_connections.Dequeue());
    }

    sealed class ControlledConnection(X509Certificate2 certificate) : IPeerTransportConnection
    {
        readonly Channel<ControlFrame?> _received = Channel.CreateUnbounded<ControlFrame?>();
        public List<ControlFrame> Sent { get; } = [];
        public bool Disposed { get; private set; }
        public int ReceiveCalls { get; private set; }
        public IPAddress RemoteAddress => IPAddress.Loopback;
        public X509Certificate2 RemoteCertificate => certificate;
        public Task SendAsync(ControlFrame frame, CancellationToken cancellationToken)
        {
            lock (Sent) Sent.Add(frame);
            return Task.CompletedTask;
        }
        public async Task<ControlFrame?> ReceiveAsync(CancellationToken cancellationToken)
        {
            ReceiveCalls++;
            return await _received.Reader.ReadAsync(cancellationToken);
        }
        public void Receive(ControlFrame frame) => _received.Writer.TryWrite(frame);
        public void Complete() => _received.Writer.TryWrite(null);
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _received.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
    [Fact]
    public async Task ApprovedConnection_RoutesFramesAndReportsDrop_WithoutLiveTls()
    {
        var local = Store("controlled-local");
        var remote = Store("controlled-remote");
        var approved = Peer(remote, "Remote PC");
        local.Approve(approved);
        var listener = new FakeListener();
        var connection = new ControlledConnection(remote.Identity.Certificate);
        var connector = new FakeConnector(connection);
        await using var host = new LanPairingHost(local, _ => null, listener, connector);
        var connectionChanges = 0;
        var received = new TaskCompletionSource<ControlFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dropped = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.ConnectionsChanged += () => Interlocked.Increment(ref connectionChanges);
        host.ApplicationFrameReceived += (_, frame) => received.TrySetResult(frame);
        host.DeliveryConfirmed += (_, id) => delivered.TrySetResult(id);
        host.ConnectionDropped += id => dropped.TrySetResult(id);

        await host.ConnectApprovedAsync(new IPEndPoint(IPAddress.Loopback, 41000), approved, CancellationToken.None);
        Assert.Contains(remote.Identity.PeerId, host.ConnectedPeerIds);
        Assert.Contains(connection.Sent, frame => frame.Type == ControlMessageType.Hello);

        var hello = Hello.Current(Capability.Text | Capability.AttentionCards).ToFrame(Guid.NewGuid());
        connection.Receive(hello);
        await WaitUntilAsync(() => host.PeerCapabilities(remote.Identity.PeerId).HasFlag(Capability.AttentionCards));

        var receiptId = Guid.NewGuid();
        connection.Receive(new ControlFrame
        {
            Type = ControlMessageType.Delivered,
            MessageId = Guid.NewGuid(),
            CorrelationId = receiptId,
            Payload = [],
        });
        Assert.Equal(receiptId, await delivered.Task.WaitAsync(TimeSpan.FromSeconds(2)));

        connection.Receive(new ControlFrame
        {
            Type = ControlMessageType.PairingNonce,
            MessageId = Guid.NewGuid(),
            Payload = [],
        });
        var chat = new ControlFrame
        {
            Type = ControlMessageType.Chat,
            MessageId = Guid.NewGuid(),
            Payload = [1],
        };
        connection.Receive(chat);
        Assert.Equal(chat, await received.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        await WaitUntilAsync(() => connection.Sent.Any(frame =>
            frame.Type == ControlMessageType.Delivered && frame.CorrelationId == chat.MessageId));

        connection.Complete();
        Assert.Equal(remote.Identity.PeerId, await dropped.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.DoesNotContain(remote.Identity.PeerId, host.ConnectedPeerIds);
        Assert.True(connectionChanges >= 3);
        Assert.True(listener.Disposed is false);
    }

    [Fact]
    public async Task DuplicateApprovedConnection_KeepsFirstConnection()
    {
        var local = Store("duplicate-local");
        var remote = Store("duplicate-remote");
        var approved = Peer(remote, "Remote PC");
        local.Approve(approved);
        var first = new ControlledConnection(remote.Identity.Certificate);
        var second = new ControlledConnection(remote.Identity.Certificate);
        var connector = new QueueConnector(first, second);
        await using var host = new LanPairingHost(local, _ => null, new FakeListener(), connector);

        await host.ConnectApprovedAsync(new IPEndPoint(IPAddress.Loopback, 41000), approved, CancellationToken.None);
        await host.ConnectApprovedAsync(new IPEndPoint(IPAddress.Loopback, 41001), approved, CancellationToken.None);

        Assert.Single(host.ConnectedPeerIds);
        Assert.Empty(second.Sent);
    }

    [Fact]
    public async Task PeerSpecificAdapters_ForwardOnlyTheirPeerAndProtocolFrames()
    {
        var local = Store("adapter-local");
        var remote = Store("adapter-remote");
        var approved = Peer(remote, "Remote PC");
        local.Approve(approved);
        var connection = new ControlledConnection(remote.Identity.Certificate);
        await using var host = new LanPairingHost(local, _ => null, new FakeListener(), new FakeConnector(connection));
        var chat = host.CreateChatTransport(remote.Identity.PeerId);
        var attention = host.CreateAttentionCardTransport(remote.Identity.PeerId);
        var audio = host.CreateAudioControlTransport(remote.Identity.PeerId);
        var chatFrames = new List<ControlFrame>();
        var cardFrames = new List<ControlFrame>();
        var audioFrames = new List<ControlFrame>();
        chat.FrameReceived += chatFrames.Add;
        attention.FrameReceived += cardFrames.Add;
        audio.FrameReceived += audioFrames.Add;

        await host.ConnectApprovedAsync(new IPEndPoint(IPAddress.Loopback, 41000), approved, CancellationToken.None);
        connection.Receive(Hello.Current(Capability.Text | Capability.AttentionCards | Capability.SendAudio).ToFrame(Guid.NewGuid()));
        connection.Receive(new ControlFrame { Type = ControlMessageType.Chat, MessageId = Guid.NewGuid(), Payload = [1] });
        connection.Receive(new ControlFrame { Type = ControlMessageType.AttentionCard, MessageId = Guid.NewGuid(), Payload = [1] });
        connection.Receive(new ControlFrame { Type = ControlMessageType.AudioSessionOffer, MessageId = Guid.NewGuid(), Payload = [1] });

        await WaitUntilAsync(() => chatFrames.Count == 1 && cardFrames.Count == 1 && audioFrames.Count == 1);
        Assert.Equal(ControlMessageType.Chat, chatFrames[0].Type);
        Assert.Equal(ControlMessageType.AttentionCard, cardFrames[0].Type);
        Assert.Equal(ControlMessageType.AudioSessionOffer, audioFrames[0].Type);
    }

    [Fact]
    public async Task PairingPromotion_KeepsTheFirstTransportAndDisposesADuplicate()
    {
        var local = Store("promotion-local");
        var remote = Store("promotion-remote");
        await using var host = new LanPairingHost(local, _ => null, new FakeListener(), new FakeConnector(new ControlledConnection(remote.Identity.Certificate)));
        var firstConnection = new ControlledConnection(remote.Identity.Certificate);
        var duplicateConnection = new ControlledConnection(remote.Identity.Certificate);
        var first = new LanPairingHost.TlsPairingTransport(firstConnection, null);
        var duplicate = new LanPairingHost.TlsPairingTransport(duplicateConnection, null);
        var changes = 0;
        host.ConnectionsChanged += () => changes++;

        host.PromotePairingConnection(remote.Identity.PeerId, first);
        host.PromotePairingConnection(remote.Identity.PeerId, duplicate);
        await WaitUntilAsync(() => duplicateConnection.Disposed);

        Assert.Contains(remote.Identity.PeerId, host.ConnectedPeerIds);
        Assert.Contains(firstConnection.Sent, frame => frame.Type == ControlMessageType.Hello);
        Assert.Empty(duplicateConnection.Sent);
        Assert.Equal(1, changes);
        Assert.Equal(0, firstConnection.ReceiveCalls);
    }

    [Fact]
    public async Task PairingPromotion_OfTheSameTransportRemainsSelected()
    {
        var local = Store("same-promotion-local");
        var remote = Store("same-promotion-remote");
        await using var host = new LanPairingHost(local, _ => null, new FakeListener(), new FakeConnector(new ControlledConnection(remote.Identity.Certificate)));
        var connection = new ControlledConnection(remote.Identity.Certificate);
        var transport = new LanPairingHost.TlsPairingTransport(connection, null);

        host.PromotePairingConnection(remote.Identity.PeerId, transport);
        host.PromotePairingConnection(remote.Identity.PeerId, transport);

        Assert.False(connection.Disposed);
        Assert.Equal(2, connection.Sent.Count(frame => frame.Type == ControlMessageType.Hello));
    }
}
