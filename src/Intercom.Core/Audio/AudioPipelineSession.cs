using System.Threading.Channels;
using Intercom.Diagnostics;

namespace Intercom.Audio;

/// <summary>Owns one bidirectional audio session. Capture callbacks enqueue
/// bounded PCM only; encoding, UDP, decoding, jitter, and playout run on
/// independent background loops.</summary>
public sealed class AudioPipelineSession : IAsyncDisposable
{
    readonly IAudioDevice _device;
    readonly UdpAudioSender _sender;
    readonly UdpAudioReceiver _receiver;
    readonly IAudioEncoder _encoder;
    readonly IAudioDecoder _decoder;
    readonly AdaptiveJitterBuffer _jitter = new();
    readonly Channel<short[]> _captureQueue = Channel.CreateBounded<short[]>(new BoundedChannelOptions(12)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false,
    });
    readonly CancellationTokenSource _cts = new();
    readonly Guid _sessionId;
    readonly Guid _sendStreamId;
    readonly Guid _receiveStreamId;
    readonly object _gate = new();
    Task[] _loops = [];
    ulong _sequence;
    bool _transmitting;
    bool _disposed;
    bool _resourcesDisposed;
    long _capturedSamples;
    long _sentPackets;
    long _receivedPackets;
    long _playedFrames;
    int _capturePeak;

    public AudioPipelineSession(
        Guid sessionId,
        Guid sendStreamId,
        Guid receiveStreamId,
        IAudioDevice device,
        UdpAudioSender sender,
        UdpAudioReceiver receiver,
        IAudioEncoder? encoder = null,
        IAudioDecoder? decoder = null)
    {
        _sessionId = sessionId;
        _sendStreamId = sendStreamId;
        _receiveStreamId = receiveStreamId;
        _device = device;
        _sender = sender;
        _receiver = receiver;
        _encoder = encoder ?? new ConcentusAudioEncoder();
        _decoder = decoder ?? new ConcentusAudioDecoder();
        _device.Captured += OnCaptured;
        _device.DeviceFailed += OnDeviceFailed;
    }

    public AudioSessionStateMachine State { get; } = new();
    public bool Transmitting { get { lock (_gate) return _transmitting; } }
    public AudioPipelineDiagnostics Diagnostics => new(
        _device.InputDeviceName,
        _device.OutputDeviceName,
        Interlocked.Read(ref _capturedSamples),
        Interlocked.Read(ref _sentPackets),
        Interlocked.Read(ref _receivedPackets),
        Interlocked.Read(ref _playedFrames),
        _jitter.ConcealedFrames,
        Volatile.Read(ref _capturePeak));

    public async Task StartAsync()
    {
        if (!State.Start()) throw new InvalidOperationException("Audio session cannot start from its current state.");
        try
        {
            await _device.StartAsync().ConfigureAwait(false);
            _loops = [EncodeAndSendAsync(_cts.Token), ReceiveAsync(_cts.Token), PlayoutAsync(_cts.Token)];
            State.Started();
        }
        catch
        {
            State.Fail();
            throw;
        }
    }

    public void StartTransmitting() { lock (_gate) _transmitting = true; }
    public void StopTransmitting() { lock (_gate) _transmitting = false; }

    void OnCaptured(short[] samples)
    {
        lock (_gate) { if (!_transmitting) return; }
        Interlocked.Add(ref _capturedSamples, samples.Length);
        var peak = samples.Length == 0 ? 0 : samples.Max(value => Math.Abs((int)value));
        Volatile.Write(ref _capturePeak, peak);
        _captureQueue.Writer.TryWrite(samples);
    }

    void OnDeviceFailed(Exception error)
    {
        State.Fail();
        _cts.Cancel();
    }

    async Task EncodeAndSendAsync(CancellationToken cancellationToken)
    {
        var pending = new List<short>(AudioFormat.SamplesPerFrame * 2);
        try
        {
            await foreach (var quantum in _captureQueue.Reader.ReadAllAsync(cancellationToken))
            {
                pending.AddRange(quantum);
                while (pending.Count >= AudioFormat.SamplesPerFrame)
                {
                    var pcm = pending.GetRange(0, AudioFormat.SamplesPerFrame).ToArray();
                    pending.RemoveRange(0, AudioFormat.SamplesPerFrame);
                    var sequence = _sequence++;
                    var encoded = _encoder.Encode(pcm);
                    await _sender.SendAsync(new AudioPacket
                    {
                        SessionId = _sessionId,
                        StreamId = _sendStreamId,
                        Sequence = sequence,
                        SampleTimestamp = sequence * AudioFormat.SamplesPerFrame,
                        Flags = AudioPacketFlags.None,
                        OpusPayload = encoded,
                    }, cancellationToken).ConfigureAwait(false);
                    Interlocked.Increment(ref _sentPackets);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { DiagnosticLog.Current.Error("audio.send-loop-failed", $"session={_sessionId}", ex); State.Fail(); _cts.Cancel(); }
    }

    async Task ReceiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var packet = await _receiver.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (packet is null) continue;
                if (packet.SessionId != _sessionId || packet.StreamId != _receiveStreamId) continue;
                Interlocked.Increment(ref _receivedPackets);
                _jitter.Add(new JitterFrame(packet.Sequence, packet.SampleTimestamp, packet.OpusPayload));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { DiagnosticLog.Current.Error("audio.receive-loop-failed", $"session={_sessionId}", ex); State.Fail(); _cts.Cancel(); }
    }

    async Task PlayoutAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(AudioFormat.FrameMilliseconds));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var next = _jitter.Read();
                if (next.Frame is null && !next.Conceal) continue;
                var pcm = next.Conceal ? _decoder.ConcealLoss() : _decoder.Decode(next.Frame!.Payload);
                _device.QueuePlayback(pcm);
                Interlocked.Increment(ref _playedFrames);
                if (next.Conceal) State.Degrade(); else State.Recover();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { DiagnosticLog.Current.Error("audio.playout-loop-failed", $"session={_sessionId}", ex); State.Fail(); _cts.Cancel(); }
    }

    public void PlayTestTone()
    {
        for (var frameIndex = 0; frameIndex < 15; frameIndex++)
        {
            var pcm = new short[AudioFormat.SamplesPerFrame];
            for (var i = 0; i < pcm.Length; i++)
            {
                var sampleIndex = frameIndex * pcm.Length + i;
                pcm[i] = (short)(Math.Sin(sampleIndex * 2 * Math.PI * 440 / AudioFormat.SampleRate) * short.MaxValue * 0.15);
            }
            _device.QueuePlayback(pcm);
        }
    }

    public async Task StopAsync()
    {
        if (!State.BeginStop()) return;
        StopTransmitting();
        _cts.Cancel();
        _captureQueue.Writer.TryComplete();
        try { await Task.WhenAll(_loops).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _jitter.Reset();
        await DisposeResourcesAsync().ConfigureAwait(false);
        State.Stopped();
    }

    async Task DisposeResourcesAsync()
    {
        lock (_gate)
        {
            if (_resourcesDisposed) return;
            _resourcesDisposed = true;
        }
        await _device.DisposeAsync().ConfigureAwait(false);
        await _sender.DisposeAsync().ConfigureAwait(false);
        await _receiver.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (State.State == AudioSessionState.Stopped)
            await DisposeResourcesAsync().ConfigureAwait(false);
        else
            await StopAsync().ConfigureAwait(false);
        _device.Captured -= OnCaptured;
        _device.DeviceFailed -= OnDeviceFailed;
        _cts.Dispose();
    }
}

public sealed record AudioPipelineDiagnostics(
    string InputDeviceName,
    string OutputDeviceName,
    long CapturedSamples,
    long SentPackets,
    long ReceivedPackets,
    long PlayedFrames,
    long ConcealedFrames,
    int CapturePeak);
