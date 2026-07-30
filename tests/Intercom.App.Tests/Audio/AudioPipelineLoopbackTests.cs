using System.Threading.Channels;
using System.Net;
using Intercom.Audio;
using Xunit;

namespace Intercom.App.Tests.Audio;

public sealed class AudioPipelineLoopbackTests
{
    [Fact]
    public async Task TalkBurstTraversesProductionCodecUdpJitterAndPlayback()
    {
        var device = new LoopbackDevice();
        await using var loopback = await StartLoopbackAsync(device);
        loopback.StartTransmitting();
        var spoken = Enumerable.Range(0, AudioFormat.SamplesPerFrame * 4)
            .Select(index => (short)(Math.Sin(index * 2 * Math.PI * 440 / AudioFormat.SampleRate) * 8_000))
            .ToArray();
        foreach (var frame in spoken.Chunk(AudioFormat.SamplesPerFrame)) device.Emit(frame);

        var played = await device.Played.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(AudioFormat.SamplesPerFrame, played.Length);
        Assert.True(played.Max(sample => Math.Abs((int)sample)) > 1_000);
    }

    [Fact]
    public async Task SilenceRemainsSilentAcrossCodecAndPlayback()
    {
        var device = new LoopbackDevice();
        await using var loopback = await StartLoopbackAsync(device);
        loopback.StartTransmitting();
        for (var i = 0; i < 4; i++) device.Emit(new short[AudioFormat.SamplesPerFrame]);

        var played = await device.Played.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(played.Max(sample => Math.Abs((int)sample)) <= 2);
    }

    [Fact]
    public async Task SecondTalkBurstPlaysAfterIdleGapOnSameSession()
    {
        var device = new LoopbackDevice();
        await using var loopback = await StartLoopbackAsync(device);
        var tone = Enumerable.Range(0, AudioFormat.SamplesPerFrame)
            .Select(index => (short)(Math.Sin(index * 2 * Math.PI * 330 / AudioFormat.SampleRate) * 6_000))
            .ToArray();

        loopback.StartTransmitting();
        for (var i = 0; i < 4; i++) device.Emit(tone);
        Assert.True((await ReadAudibleAsync(device)).Length > 0);
        loopback.StopTransmitting();
        await Task.Delay(TimeSpan.FromMilliseconds(250));
        while (device.Played.Reader.TryRead(out _)) { }

        loopback.StartTransmitting();
        for (var i = 0; i < 4; i++) device.Emit(tone);

        Assert.True((await ReadAudibleAsync(device)).Max(sample => Math.Abs((int)sample)) > 1_000);
    }

    [Fact]
    public async Task ReceiveOnlyDeviceStartsAndCannotBePutIntoTransmitMode()
    {
        var device = new LoopbackDevice(canCapture: false, canRender: true);
        await using var loopback = await StartLoopbackAsync(device);

        loopback.StartTransmitting();
        loopback.PlayTestTone();

        Assert.False(loopback.CanTransmit);
        Assert.True(loopback.CanReceive);
        Assert.False(loopback.Transmitting);
        Assert.True(await device.Played.Reader.WaitToReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
    }

    static async Task<short[]> ReadAudibleAsync(LoopbackDevice device)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (await device.Played.Reader.WaitToReadAsync(timeout.Token))
            while (device.Played.Reader.TryRead(out var pcm))
                if (pcm.Max(sample => Math.Abs((int)sample)) > 100) return pcm;
        throw new TimeoutException("No audible frame reached local playback.");
    }

    static async Task<AudioPipelineSession> StartLoopbackAsync(IAudioDevice device)
    {
        var material = AudioSessionKeyMaterial.Create();
        var receiver = new UdpAudioReceiver(0, material.Key, material.NoncePrefix);
        var sender = new UdpAudioSender(
            new IPEndPoint(IPAddress.Loopback, receiver.LocalPort),
            material.Key,
            material.NoncePrefix);
        var streamId = Guid.NewGuid();
        var session = new AudioPipelineSession(
            Guid.NewGuid(), streamId, streamId, device, sender, receiver);
        try
        {
            await session.StartAsync();
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    sealed class LoopbackDevice : IAudioDevice
    {
        readonly bool _canCapture;
        readonly bool _canRender;

        public LoopbackDevice(bool canCapture = true, bool canRender = true)
        {
            _canCapture = canCapture;
            _canRender = canRender;
        }

        public string InputDeviceName => "Loopback microphone";
        public string OutputDeviceName => "Loopback speaker";
        public bool CanCapture => _canCapture;
        public bool CanRender => _canRender;
        public event Action<short[]>? Captured;
        public event Action<Exception>? DeviceFailed { add { } remove { } }
        public Channel<short[]> Played { get; } = Channel.CreateUnbounded<short[]>();
        public Task StartAsync() => Task.CompletedTask;
        public void Emit(short[] pcm) => Captured?.Invoke(pcm);
        public void QueuePlayback(short[] pcm) => Played.Writer.TryWrite(pcm);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
