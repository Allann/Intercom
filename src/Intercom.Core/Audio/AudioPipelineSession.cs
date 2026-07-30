using System.Threading.Channels;
using Intercom.Diagnostics;

namespace Intercom.Audio;

/// <summary>Owns one bidirectional audio session. Capture callbacks enqueue
/// bounded PCM only; encoding, UDP, decoding, jitter, and playout run on
/// independent background loops.</summary>
public sealed class AudioPipelineSession : IAsyncDisposable
{
    sealed record CaptureItem(short[]? Samples, bool EndOfTalkspurt, long SourceUnixMilliseconds);

    readonly IAudioDevice _device;
    readonly UdpAudioSender _sender;
    readonly UdpAudioReceiver _receiver;
    readonly IAudioEncoder _encoder;
    readonly IAudioDecoder _decoder;
    readonly AdaptiveJitterBuffer _jitter = new();
    readonly Channel<CaptureItem> _captureQueue = Channel.CreateBounded<CaptureItem>(new BoundedChannelOptions(12)
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
    readonly AudioMeasurementImpairment _impairment = new(AudioMeasurementSettings.Load());
    bool MeasurementEnabled => _impairment.Profile != AudioMeasurementProfile.Disabled;
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
    bool _receiveTalkspurtActive;

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
    public Guid SessionId => _sessionId;
    public bool CanTransmit => _device.CanCapture;
    public bool CanReceive => _device.CanRender;
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

    public void StartTransmitting()
    {
        lock (_gate)
        {
            if (_transmitting || !_device.CanCapture) return;
            _transmitting = true;
        }
        if (MeasurementEnabled)
            DiagnosticLog.Current.Info("audio.measurement-talk-start", $"session={_sessionId} profile={_impairment.Profile.ToConfigValue()} sourceUnixMs={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
    }

    public void StopTransmitting()
    {
        lock (_gate)
        {
            if (!_transmitting) return;
            _transmitting = false;
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _captureQueue.Writer.TryWrite(new CaptureItem(null, true, now));
        if (MeasurementEnabled)
            DiagnosticLog.Current.Info("audio.measurement-talk-stop", $"session={_sessionId} profile={_impairment.Profile.ToConfigValue()} sourceUnixMs={now}");
    }

    void OnCaptured(short[] samples)
    {
        lock (_gate) { if (!_transmitting) return; }
        Interlocked.Add(ref _capturedSamples, samples.Length);
        var peak = samples.Length == 0 ? 0 : samples.Max(value => Math.Abs((int)value));
        Volatile.Write(ref _capturePeak, peak);
        _captureQueue.Writer.TryWrite(new CaptureItem(samples, false, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
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
            await foreach (var item in _captureQueue.Reader.ReadAllAsync(cancellationToken))
            {
                if (item.EndOfTalkspurt)
                {
                    pending.Clear();
                    await SendPacketAsync([], AudioPacketFlags.EndOfTalkspurt, item.SourceUnixMilliseconds, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                pending.AddRange(item.Samples!);
                while (pending.Count >= AudioFormat.SamplesPerFrame)
                {
                    var pcm = pending.GetRange(0, AudioFormat.SamplesPerFrame).ToArray();
                    pending.RemoveRange(0, AudioFormat.SamplesPerFrame);
                    var encoded = _encoder.Encode(pcm);
                    await SendPacketAsync(encoded, AudioPacketFlags.None, item.SourceUnixMilliseconds, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { DiagnosticLog.Current.Error("audio.send-loop-failed", $"session={_sessionId}", ex); State.Fail(); _cts.Cancel(); }
    }

    async Task SendPacketAsync(byte[] payload, AudioPacketFlags flags, long sourceUnixMilliseconds, CancellationToken cancellationToken)
    {
        var sequence = _sequence++;
        if (_impairment.ShouldDrop(sequence, flags))
        {
            DiagnosticLog.Current.Info("audio.measurement-packet-dropped", $"session={_sessionId} sequence={sequence} profile={_impairment.Profile.ToConfigValue()}");
            return;
        }
        await _sender.SendAsync(new AudioPacket
        {
            SessionId = _sessionId,
            StreamId = _sendStreamId,
            Sequence = sequence,
            SampleTimestamp = checked((ulong)sourceUnixMilliseconds),
            Flags = flags,
            OpusPayload = payload,
        }, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _sentPackets);
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
                if (!_receiveTalkspurtActive && packet.Flags == AudioPacketFlags.None)
                {
                    _receiveTalkspurtActive = true;
                    if (MeasurementEnabled) LogMeasuredLatency("audio.measurement-first-received", packet);
                }
                _jitter.Add(new JitterFrame(packet.Sequence, packet.SampleTimestamp, packet.OpusPayload, packet.Flags));
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
                if (next.Frame is { Flags: AudioPacketFlags.EndOfTalkspurt } marker)
                {
                    _receiveTalkspurtActive = false;
                    if (MeasurementEnabled) LogMeasuredLatency("audio.measurement-release-to-speaker-queue", new AudioPacket
                    {
                        SessionId = _sessionId, StreamId = _receiveStreamId, Sequence = marker.Sequence,
                        SampleTimestamp = marker.SampleTimestamp, Flags = marker.Flags, OpusPayload = marker.Payload,
                    });
                    continue;
                }
                var pcm = next.Conceal ? _decoder.ConcealLoss() : _decoder.Decode(next.Frame!.Payload);
                _device.QueuePlayback(pcm);
                Interlocked.Increment(ref _playedFrames);
                if (MeasurementEnabled && next.Frame is { } measuredFrame)
                    LogMeasuredLatency("audio.measurement-frame-to-speaker-queue", new AudioPacket
                    {
                        SessionId = _sessionId, StreamId = _receiveStreamId, Sequence = measuredFrame.Sequence,
                        SampleTimestamp = measuredFrame.SampleTimestamp, Flags = measuredFrame.Flags, OpusPayload = measuredFrame.Payload,
                    });
                if (next.Conceal) State.Degrade(); else State.Recover();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { DiagnosticLog.Current.Error("audio.playout-loop-failed", $"session={_sessionId}", ex); State.Fail(); _cts.Cancel(); }
    }

    void LogMeasuredLatency(string eventName, AudioPacket packet)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var source = checked((long)packet.SampleTimestamp);
        DiagnosticLog.Current.Info(eventName,
            $"session={_sessionId} profile={_impairment.Profile.ToConfigValue()} sequence={packet.Sequence} sourceUnixMs={source} observedUnixMs={now} estimatedMs={now - source} targetFrames={_jitter.TargetFrames} occupancy={_jitter.Occupancy} concealed={_jitter.ConcealedFrames} discarded={_jitter.DiscardedFrames}");
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
