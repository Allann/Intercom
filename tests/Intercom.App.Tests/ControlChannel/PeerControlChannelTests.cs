using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Intercom.ControlChannel;
using Intercom.Identity;
using Intercom.Presence;
using Xunit;

namespace Intercom.App.Tests.ControlChannel;

/// <summary>
/// Orchestration-level tests for <see cref="PeerControlChannel"/>, driven by
/// hand-written fakes for the transport seam — the same pattern
/// DiscoveryServiceTests uses for IDnsServiceDiscovery (FakeDnsServiceDiscovery)
/// to test DiscoveryService without real sockets. No mocking framework, per
/// this codebase's test convention.
/// </summary>
public class PeerControlChannelTests
{
    static readonly IPEndPoint RemoteEndpoint = new(IPAddress.Loopback, 5001);
    static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    static SpkiPin LocalSpki() => Pin(1);   // lower than RemoteSpkiForTieBreak -> local initiates
    static SpkiPin RemoteSpkiForTieBreak() => Pin(2);
    static SpkiPin HigherLocalSpki() => Pin(9); // higher than RemoteSpkiForTieBreak -> local waits

    static SpkiPin Pin(byte seed) => new(Enumerable.Repeat(seed, 32).ToArray());

    [Fact]
    public async Task EvaluateConnectAsync_LowerLocalHash_DialsAndCompletesHandshake()
    {
        using var remoteCert = SelfSignedCert();
        var connector = new FakeConnector();
        var connection = new FakeTransportConnection(remoteCert);
        connector.NextConnection = connection;
        connection.EnqueueReceive(Hello.Current(Capability.Text).ToFrame(Guid.NewGuid()));

        var channel = new PeerControlChannel(connector, LocalSpki(), Capability.Text, () => []);
        channel.OnDiscovered();

        await channel.EvaluateConnectAsync(RemoteEndpoint, RemoteSpkiForTieBreak(), CancellationToken.None);

        Assert.Single(connector.DialedEndpoints);
        Assert.IsType<ConnState.Connected>(channel.State);
        Assert.IsType<ConnectionTrust.PairingOnly>(channel.Trust); // remote cert isn't in the (empty) approved registry
        Assert.Contains(connection.Sent, f => f.Type == ControlMessageType.Hello);

        await channel.DisposeAsync();
    }

    [Fact]
    public async Task EvaluateConnectAsync_HigherLocalHash_NeverDials()
    {
        var connector = new FakeConnector();
        var channel = new PeerControlChannel(connector, HigherLocalSpki(), Capability.Text, () => []);

        await channel.EvaluateConnectAsync(RemoteEndpoint, RemoteSpkiForTieBreak(), CancellationToken.None);

        Assert.Empty(connector.DialedEndpoints);
        Assert.IsType<ConnState.Idle>(channel.State);
    }

    [Fact]
    public async Task EvaluateConnectAsync_ConnectorThrows_MovesToReconnecting()
    {
        var connector = new FakeConnector { FailWith = new IOException("connection refused") };
        var channel = new PeerControlChannel(connector, LocalSpki(), Capability.Text, () => []);
        channel.OnDiscovered();

        await channel.EvaluateConnectAsync(RemoteEndpoint, RemoteSpkiForTieBreak(), CancellationToken.None);

        Assert.IsType<ConnState.Reconnecting>(channel.State);
    }

    [Fact]
    public async Task EvaluateConnectAsync_ApprovedPeer_TrustIsApproved()
    {
        using var remoteCert = SelfSignedCert();
        var approvedPeer = MakeApprovedPeer(remoteCert);
        var connector = new FakeConnector();
        var connection = new FakeTransportConnection(remoteCert);
        connector.NextConnection = connection;
        connection.EnqueueReceive(Hello.Current(Capability.Text).ToFrame(Guid.NewGuid()));

        var channel = new PeerControlChannel(connector, LocalSpki(), Capability.Text, () => [approvedPeer]);
        channel.OnDiscovered();

        await channel.EvaluateConnectAsync(RemoteEndpoint, RemoteSpkiForTieBreak(), CancellationToken.None);

        var trust = Assert.IsType<ConnectionTrust.Approved>(channel.Trust);
        Assert.Equal(approvedPeer.PeerId, trust.PeerId);

        await channel.DisposeAsync();
    }

    [Fact]
    public async Task EvaluateConnectAsync_ExpectedPeerPinMismatch_SurfacesIdentityChanged_NeverPairingOnly()
    {
        using var previousCert = SelfSignedCert();
        using var newCert = SelfSignedCert();
        var expectedPeer = MakeApprovedPeer(previousCert);

        var connector = new FakeConnector();
        var connection = new FakeTransportConnection(newCert); // presents a DIFFERENT cert than expected
        connector.NextConnection = connection;

        var channel = new PeerControlChannel(connector, LocalSpki(), Capability.Text, () => [expectedPeer], expectedApprovedPeer: expectedPeer);
        channel.OnDiscovered();
        PeerIdentityOutcome.IdentityChanged? raised = null;
        channel.IdentityChanged += changed => raised = changed;

        await channel.EvaluateConnectAsync(RemoteEndpoint, RemoteSpkiForTieBreak(), CancellationToken.None);

        Assert.NotNull(raised);
        Assert.Same(expectedPeer, raised!.PreviouslyApproved);
        Assert.Null(channel.Trust); // never fell back to PairingOnly
        Assert.IsType<ConnState.Reconnecting>(channel.State);
        Assert.True(connection.Disposed);
    }

    [Fact]
    public async Task AcceptInboundAsync_WhileAlreadyConnected_DisposesTheNewConnection()
    {
        using var remoteCert = SelfSignedCert();
        var connector = new FakeConnector();
        var firstConnection = new FakeTransportConnection(remoteCert);
        connector.NextConnection = firstConnection;
        firstConnection.EnqueueReceive(Hello.Current(Capability.Text).ToFrame(Guid.NewGuid()));

        var channel = new PeerControlChannel(connector, LocalSpki(), Capability.Text, () => []);
        channel.OnDiscovered();
        await channel.EvaluateConnectAsync(RemoteEndpoint, RemoteSpkiForTieBreak(), CancellationToken.None);
        Assert.IsType<ConnState.Connected>(channel.State);

        var secondConnection = new FakeTransportConnection(remoteCert);
        await channel.AcceptInboundAsync(secondConnection, CancellationToken.None);

        Assert.True(secondConnection.Disposed);
        Assert.IsType<ConnState.Connected>(channel.State); // original connection undisturbed

        await channel.DisposeAsync();
    }

    [Fact]
    public async Task ReceiveLoop_AcceptedApplicationFrame_RaisesMessageReceived_AndSendsDeliveredReceipt()
    {
        using var remoteCert = SelfSignedCert();
        var approvedPeer = MakeApprovedPeer(remoteCert);
        var connector = new FakeConnector();
        var connection = new FakeTransportConnection(remoteCert);
        connector.NextConnection = connection;
        connection.EnqueueReceive(Hello.Current(Capability.Text).ToFrame(Guid.NewGuid()));

        var channel = new PeerControlChannel(connector, LocalSpki(), Capability.Text, () => [approvedPeer]);
        channel.OnDiscovered();
        var received = new TaskCompletionSource<ControlFrame>();
        channel.MessageReceived += f => received.TrySetResult(f);

        await channel.EvaluateConnectAsync(RemoteEndpoint, RemoteSpkiForTieBreak(), CancellationToken.None);

        var appFrame = new ControlFrame { Type = (ControlMessageType)100, MessageId = Guid.NewGuid(), Payload = [7] };
        connection.EnqueueReceive(appFrame);

        var result = await received.Task.WaitAsync(WaitTimeout);
        Assert.Equal(appFrame.MessageId, result.MessageId);

        await WaitUntilAsync(() => connection.Sent.Any(f => f.Type == ControlMessageType.Delivered && f.CorrelationId == appFrame.MessageId));

        await channel.DisposeAsync();
    }

    [Fact]
    public async Task ReceiveLoop_PeerClosesCleanly_DropsToReconnecting()
    {
        using var remoteCert = SelfSignedCert();
        var connector = new FakeConnector();
        var connection = new FakeTransportConnection(remoteCert);
        connector.NextConnection = connection;
        connection.EnqueueReceive(Hello.Current(Capability.Text).ToFrame(Guid.NewGuid()));

        var channel = new PeerControlChannel(connector, LocalSpki(), Capability.Text, () => []);
        channel.OnDiscovered();
        var dropped = new TaskCompletionSource();
        channel.StateChanged += (_, newState) =>
        {
            if (newState is ConnState.Reconnecting) dropped.TrySetResult();
        };

        await channel.EvaluateConnectAsync(RemoteEndpoint, RemoteSpkiForTieBreak(), CancellationToken.None);
        connection.EnqueueClose();

        await dropped.Task.WaitAsync(WaitTimeout);
        Assert.IsType<ConnState.Reconnecting>(channel.State);

        await channel.DisposeAsync();
    }

    [Fact]
    public async Task PairingOnlyConnection_ApplicationFrameFromPeer_IsRejectedAndDropsTheConnection()
    {
        using var remoteCert = SelfSignedCert();
        var connector = new FakeConnector();
        var connection = new FakeTransportConnection(remoteCert);
        connector.NextConnection = connection;
        connection.EnqueueReceive(Hello.Current(Capability.Text).ToFrame(Guid.NewGuid()));

        var channel = new PeerControlChannel(connector, LocalSpki(), Capability.Text, () => []); // empty registry -> PairingOnly
        channel.OnDiscovered();
        var dropped = new TaskCompletionSource();
        channel.StateChanged += (_, newState) =>
        {
            if (newState is ConnState.Reconnecting) dropped.TrySetResult();
        };
        var received = new List<ControlFrame>();
        channel.MessageReceived += received.Add;

        await channel.EvaluateConnectAsync(RemoteEndpoint, RemoteSpkiForTieBreak(), CancellationToken.None);
        Assert.IsType<ConnectionTrust.PairingOnly>(channel.Trust);

        connection.EnqueueReceive(new ControlFrame { Type = (ControlMessageType)100, MessageId = Guid.NewGuid(), Payload = [1] });

        await dropped.Task.WaitAsync(WaitTimeout);
        Assert.Empty(received);

        await channel.DisposeAsync();
    }

    // ---- issue #23: presence broadcast ----

    static PresenceLease SamplePresenceLease() => new()
    {
        DeviceId = Guid.NewGuid(),
        ContactId = Guid.NewGuid(),
        IncarnationId = Guid.NewGuid(),
        Sequence = 1,
        Availability = AvailabilityState.Available,
        Dnd = false,
        IdleAgeBucket = IdleAgeBucket.UnderTwoMinutes,
        LeaseSeconds = 30,
        Capabilities = Capability.Text,
    };

    [Fact]
    public async Task Tick_ApprovedConnection_WithPresenceProvider_SendsPresenceFrame_Immediately()
    {
        using var remoteCert = SelfSignedCert();
        var approvedPeer = MakeApprovedPeer(remoteCert);
        var connector = new FakeConnector();
        var connection = new FakeTransportConnection(remoteCert);
        connector.NextConnection = connection;
        connection.EnqueueReceive(Hello.Current(Capability.Text).ToFrame(Guid.NewGuid()));

        var channel = new PeerControlChannel(
            connector, LocalSpki(), Capability.Text, () => [approvedPeer],
            presenceLeaseProvider: SamplePresenceLease);
        channel.OnDiscovered();
        await channel.EvaluateConnectAsync(RemoteEndpoint, RemoteSpkiForTieBreak(), CancellationToken.None);
        Assert.IsType<ConnectionTrust.Approved>(channel.Trust);

        // A fresh connection is a meaningful change — the very next Tick
        // sends presence without waiting a full heartbeat interval.
        channel.Tick(DateTimeOffset.UtcNow);

        Assert.Contains(connection.Sent, f => f.Type == ControlMessageType.Presence);

        await channel.DisposeAsync();
    }

    [Fact]
    public async Task Tick_PairingOnlyConnection_NeverSendsPresence_EvenWithProviderConfigured()
    {
        // Hard privacy rule (docs/research/active-device-presence.md): no
        // contact/DND/activity data in unauthenticated/pairing traffic.
        using var remoteCert = SelfSignedCert();
        var connector = new FakeConnector();
        var connection = new FakeTransportConnection(remoteCert);
        connector.NextConnection = connection;
        connection.EnqueueReceive(Hello.Current(Capability.Text).ToFrame(Guid.NewGuid()));

        var channel = new PeerControlChannel(
            connector, LocalSpki(), Capability.Text, () => [], // empty registry -> PairingOnly
            presenceLeaseProvider: SamplePresenceLease);
        channel.OnDiscovered();
        await channel.EvaluateConnectAsync(RemoteEndpoint, RemoteSpkiForTieBreak(), CancellationToken.None);
        Assert.IsType<ConnectionTrust.PairingOnly>(channel.Trust);

        channel.Tick(DateTimeOffset.UtcNow);
        channel.NotifyPresenceChanged();
        channel.Tick(DateTimeOffset.UtcNow);

        Assert.DoesNotContain(connection.Sent, f => f.Type == ControlMessageType.Presence);

        await channel.DisposeAsync();
    }

    [Fact]
    public async Task Tick_ApprovedConnection_NoPresenceProviderConfigured_NeverSendsPresence()
    {
        using var remoteCert = SelfSignedCert();
        var approvedPeer = MakeApprovedPeer(remoteCert);
        var connector = new FakeConnector();
        var connection = new FakeTransportConnection(remoteCert);
        connector.NextConnection = connection;
        connection.EnqueueReceive(Hello.Current(Capability.Text).ToFrame(Guid.NewGuid()));

        var channel = new PeerControlChannel(connector, LocalSpki(), Capability.Text, () => [approvedPeer]); // no presenceLeaseProvider
        channel.OnDiscovered();
        await channel.EvaluateConnectAsync(RemoteEndpoint, RemoteSpkiForTieBreak(), CancellationToken.None);

        channel.Tick(DateTimeOffset.UtcNow);

        Assert.DoesNotContain(connection.Sent, f => f.Type == ControlMessageType.Presence);

        await channel.DisposeAsync();
    }

    [Fact]
    public async Task Tick_TwiceInQuickSuccession_WithoutMeaningfulChange_OnlySendsPresenceOnce()
    {
        using var remoteCert = SelfSignedCert();
        var approvedPeer = MakeApprovedPeer(remoteCert);
        var connector = new FakeConnector();
        var connection = new FakeTransportConnection(remoteCert);
        connector.NextConnection = connection;
        connection.EnqueueReceive(Hello.Current(Capability.Text).ToFrame(Guid.NewGuid()));

        var channel = new PeerControlChannel(
            connector, LocalSpki(), Capability.Text, () => [approvedPeer],
            presenceLeaseProvider: SamplePresenceLease);
        channel.OnDiscovered();
        await channel.EvaluateConnectAsync(RemoteEndpoint, RemoteSpkiForTieBreak(), CancellationToken.None);

        var now = DateTimeOffset.UtcNow;
        channel.Tick(now); // sends immediately (fresh connection)
        channel.Tick(now); // same instant — cadence/jitter interval hasn't elapsed, not forced

        Assert.Single(connection.Sent, f => f.Type == ControlMessageType.Presence);

        await channel.DisposeAsync();
    }

    [Fact]
    public async Task NotifyPresenceChanged_ForcesAnOutOfCadenceSend_OnNextTick()
    {
        using var remoteCert = SelfSignedCert();
        var approvedPeer = MakeApprovedPeer(remoteCert);
        var connector = new FakeConnector();
        var connection = new FakeTransportConnection(remoteCert);
        connector.NextConnection = connection;
        connection.EnqueueReceive(Hello.Current(Capability.Text).ToFrame(Guid.NewGuid()));

        var channel = new PeerControlChannel(
            connector, LocalSpki(), Capability.Text, () => [approvedPeer],
            presenceLeaseProvider: SamplePresenceLease);
        channel.OnDiscovered();
        await channel.EvaluateConnectAsync(RemoteEndpoint, RemoteSpkiForTieBreak(), CancellationToken.None);

        var now = DateTimeOffset.UtcNow;
        channel.Tick(now); // the immediate post-connect send
        Assert.Single(connection.Sent, f => f.Type == ControlMessageType.Presence);

        channel.NotifyPresenceChanged();
        channel.Tick(now); // same instant, but forced -> sends again despite cadence

        Assert.Equal(2, connection.Sent.Count(f => f.Type == ControlMessageType.Presence));

        await channel.DisposeAsync();
    }

    static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was never met.");
            await Task.Delay(10);
        }
    }

    static X509Certificate2 SelfSignedCert()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Test Peer", ecdsa, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
    }

    static ApprovedPeer MakeApprovedPeer(X509Certificate2 cert) => new()
    {
        PeerId = Guid.NewGuid(),
        FriendlyName = "Test",
        SpkiSha256 = new SpkiPin(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo())),
        Certificate = cert.Export(X509ContentType.Cert),
        ApprovedAt = DateTimeOffset.UtcNow,
    };

    sealed class FakeConnector : IPeerTransportConnector
    {
        public FakeTransportConnection? NextConnection { get; set; }
        public Exception? FailWith { get; set; }
        public List<IPEndPoint> DialedEndpoints { get; } = [];

        public Task<IPeerTransportConnection> ConnectAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
        {
            DialedEndpoints.Add(endpoint);
            if (FailWith is not null) return Task.FromException<IPeerTransportConnection>(FailWith);
            return Task.FromResult<IPeerTransportConnection>(NextConnection!);
        }
    }

    sealed class FakeTransportConnection : IPeerTransportConnection
    {
        readonly Channel<ControlFrame?> _inbound = System.Threading.Channels.Channel.CreateUnbounded<ControlFrame?>();

        public FakeTransportConnection(X509Certificate2 remoteCertificate)
        {
            RemoteCertificate = remoteCertificate;
        }

        public X509Certificate2 RemoteCertificate { get; }
        public List<ControlFrame> Sent { get; } = [];
        public bool Disposed { get; private set; }

        public void EnqueueReceive(ControlFrame frame) => _inbound.Writer.TryWrite(frame);
        public void EnqueueClose() => _inbound.Writer.TryWrite(null);

        public Task SendAsync(ControlFrame frame, CancellationToken cancellationToken)
        {
            Sent.Add(frame);
            return Task.CompletedTask;
        }

        public async Task<ControlFrame?> ReceiveAsync(CancellationToken cancellationToken) =>
            await _inbound.Reader.ReadAsync(cancellationToken);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _inbound.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
