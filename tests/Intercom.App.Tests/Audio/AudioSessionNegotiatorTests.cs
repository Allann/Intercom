using System.Net;
using System.Net.Sockets;
using Intercom.Audio;
using Intercom.ControlChannel;
using Xunit;

namespace Intercom.App.Tests.Audio;

public sealed class AudioSessionNegotiatorTests
{
    [Fact]
    public async Task NegotiatesKeysOverControlAndStreamsEncryptedOpusOverUdp()
    {
        var (aControl, bControl) = FakeControlTransport.Pair();
        var aDevice = new FakeAudioDevice();
        var bDevice = new FakeAudioDevice();
        await using var a = new AudioSessionNegotiator(aControl, IPAddress.IPv6Loopback, () => aDevice, 0);
        await using var b = new AudioSessionNegotiator(bControl, IPAddress.IPv6Loopback, () => bDevice, 0);
        var aReady = new TaskCompletionSource<AudioPipelineSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bReady = new TaskCompletionSource<AudioPipelineSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        a.SessionReady += session => aReady.TrySetResult(session);
        b.SessionReady += session => bReady.TrySetResult(session);
        b.IncomingOffer += (offer, id) => _ = b.AcceptAsync(offer, id, CancellationToken.None);

        await a.OfferAsync(CancellationToken.None);
        var aSession = await aReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var bSession = await bReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await aSession.StartAsync();
        await bSession.StartAsync();
        aSession.StartTransmitting();
        var pcm = Enumerable.Range(0, AudioFormat.SamplesPerFrame).Select(i => (short)(i % 100)).ToArray();
        aDevice.Emit(pcm);
        aDevice.Emit(pcm);
        aDevice.Emit(pcm);

        var played = await bDevice.Played.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AudioFormat.SamplesPerFrame, played.Length);
        await aSession.StopAsync();
        await bSession.StopAsync();
    }

    [Fact]
    public async Task OfferWithoutAnswer_TimesOutAndCanBeRetried()
    {
        var control = new BlackHoleControlTransport();
        await using var negotiator = new AudioSessionNegotiator(
            control, IPAddress.Loopback, () => new FakeAudioDevice(), 0, TimeSpan.FromMilliseconds(30));
        var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        negotiator.NegotiationFailed += reason => failed.TrySetResult(reason);

        await negotiator.OfferAsync(CancellationToken.None);

        Assert.Contains("did not answer", await failed.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        await negotiator.OfferAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HandsFreeModeReachesBothPeersAndEitherPeerCanEndSession()
    {
        var (aControl, bControl) = FakeControlTransport.Pair();
        await using var a = new AudioSessionNegotiator(aControl, IPAddress.Loopback, () => new FakeAudioDevice(), 0);
        await using var b = new AudioSessionNegotiator(bControl, IPAddress.Loopback, () => new FakeAudioDevice(), 0);
        var aReady = new TaskCompletionSource<AudioInteractionMode>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bReady = new TaskCompletionSource<AudioInteractionMode>(TaskCreationOptions.RunContinuationsAsynchronously);
        var aStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        a.ModeSessionReady += (_, mode) => aReady.TrySetResult(mode);
        b.ModeSessionReady += (_, mode) => bReady.TrySetResult(mode);
        a.SessionStopped += () => aStopped.TrySetResult();
        b.SessionStopped += () => bStopped.TrySetResult();
        b.IncomingOffer += (offer, id) => _ = b.AcceptAsync(offer, id, CancellationToken.None);

        await a.OfferAsync(CancellationToken.None, AudioInteractionMode.HandsFree);

        Assert.Equal(AudioInteractionMode.HandsFree, await aReady.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(AudioInteractionMode.HandsFree, await bReady.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        await b.StopAsync(CancellationToken.None);
        await aStopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await bStopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task RejectedOfferSurfacesReasonAndAllowsRetry()
    {
        var (aControl, bControl) = FakeControlTransport.Pair();
        await using var a = new AudioSessionNegotiator(aControl, IPAddress.Loopback, () => new FakeAudioDevice(), 0);
        await using var b = new AudioSessionNegotiator(bControl, IPAddress.Loopback, () => new FakeAudioDevice(), 0);
        var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        a.NegotiationFailed += reason => failed.TrySetResult(reason);
        b.IncomingOffer += (offer, id) => _ = b.RejectAsync(id, "Do Not Disturb", CancellationToken.None);

        await a.OfferAsync(CancellationToken.None, AudioInteractionMode.HandsFree);

        Assert.Equal("Do Not Disturb", await failed.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        await a.OfferAsync(CancellationToken.None, AudioInteractionMode.PushToTalk);
    }

    [Fact]
    public async Task RetiredFailedSession_AllowsImmediateRetry()
    {
        var (aControl, bControl) = FakeControlTransport.Pair();
        await using var a = new AudioSessionNegotiator(aControl, IPAddress.Loopback, () => new FakeAudioDevice(), 0);
        await using var b = new AudioSessionNegotiator(bControl, IPAddress.Loopback, () => new FakeAudioDevice(), 0);
        var firstReady = new TaskCompletionSource<AudioPipelineSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        a.SessionReady += session => firstReady.TrySetResult(session);
        b.IncomingOffer += (offer, id) => _ = b.AcceptAsync(offer, id, CancellationToken.None);

        await a.OfferAsync(CancellationToken.None);
        var failed = await firstReady.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await a.RetireAsync(failed);

        await a.OfferAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AcceptFailure_ReleasesItsUdpPort()
    {
        var port = ReserveAvailableUdpPort();
        await using var negotiator = new AudioSessionNegotiator(
            new ThrowingControlTransport(), IPAddress.Loopback, () => new FakeAudioDevice(), port);
        var material = AudioSessionKeyMaterial.Create();
        var offer = new AudioSessionOffer(Guid.NewGuid(), Guid.NewGuid(), 12345, material.Key, material.NoncePrefix);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            negotiator.AcceptAsync(offer, Guid.NewGuid(), CancellationToken.None));

        await using var rebound = new UdpAudioReceiver(port, material.Key, material.NoncePrefix);
        Assert.Equal(port, rebound.LocalPort);
    }

    static int ReserveAvailableUdpPort()
    {
        using var udp = new UdpClient(AddressFamily.InterNetworkV6);
        udp.Client.DualMode = true;
        udp.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        return ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    }

    sealed class FakeAudioDevice : IAudioDevice
    {
        public string InputDeviceName => "Fake microphone";
        public string OutputDeviceName => "Fake speaker";
        public event Action<short[]>? Captured;
        public event Action<Exception>? DeviceFailed { add { } remove { } }
        public TaskCompletionSource<short[]> Played { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task StartAsync() => Task.CompletedTask;
        public void Emit(short[] pcm) => Captured?.Invoke(pcm);
        public void QueuePlayback(short[] pcm) => Played.TrySetResult(pcm);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class FakeControlTransport : IAudioControlTransport
    {
        FakeControlTransport? _peer;
        public event Action<ControlFrame>? FrameReceived;
        public event Action? ConnectionDropped { add { } remove { } }
        public Task SendAsync(ControlFrame frame, CancellationToken cancellationToken)
        {
            _peer!.FrameReceived?.Invoke(frame);
            return Task.CompletedTask;
        }
        public static (FakeControlTransport A, FakeControlTransport B) Pair()
        {
            var a = new FakeControlTransport(); var b = new FakeControlTransport();
            a._peer = b; b._peer = a;
            return (a, b);
        }
    }

    sealed class BlackHoleControlTransport : IAudioControlTransport
    {
        public event Action<ControlFrame>? FrameReceived { add { } remove { } }
        public event Action? ConnectionDropped { add { } remove { } }
        public Task SendAsync(ControlFrame frame, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    sealed class ThrowingControlTransport : IAudioControlTransport
    {
        public event Action<ControlFrame>? FrameReceived { add { } remove { } }
        public event Action? ConnectionDropped { add { } remove { } }
        public Task SendAsync(ControlFrame frame, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Simulated control send failure.");
    }
}
