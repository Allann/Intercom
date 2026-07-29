using System.Net;
using Intercom.ControlChannel;
using Intercom.Diagnostics;

namespace Intercom.Audio;

/// <summary>Exchanges per-direction UDP ports and fresh key material over an
/// approved TLS control transport, then constructs the shared audio pipeline.</summary>
public sealed class AudioSessionNegotiator : IAsyncDisposable
{
    readonly IAudioControlTransport _control;
    readonly IPAddress _remoteAddress;
    readonly Func<IAudioDevice> _deviceFactory;
    readonly int _localUdpPort;
    readonly TimeSpan _offerTimeout;
    readonly object _gate = new();
    PendingOffer? _pending;
    AudioPipelineSession? _active;

    public event Action<AudioSessionOffer, Guid>? IncomingOffer;
    public event Action<AudioPipelineSession>? SessionReady;
    public event Action<string>? NegotiationFailed;

    public AudioSessionNegotiator(IAudioControlTransport control, IPAddress remoteAddress, Func<IAudioDevice> deviceFactory, int localUdpPort = 47812, TimeSpan? offerTimeout = null)
    {
        _control = control;
        _remoteAddress = remoteAddress;
        _deviceFactory = deviceFactory;
        _localUdpPort = localUdpPort;
        _offerTimeout = offerTimeout ?? TimeSpan.FromSeconds(5);
        _control.FrameReceived += OnFrame;
        _control.ConnectionDropped += OnConnectionDropped;
    }

    public async Task OfferAsync(CancellationToken cancellationToken)
    {
        var sessionId = Guid.NewGuid();
        var streamId = Guid.NewGuid();
        var material = AudioSessionKeyMaterial.Create();
        var receiver = new UdpAudioReceiver(_localUdpPort, material.Key, material.NoncePrefix);
        var offer = new AudioSessionOffer(sessionId, streamId, checked((ushort)receiver.LocalPort), material.Key, material.NoncePrefix);
        var frame = offer.ToFrame(Guid.NewGuid());
        var timeoutCts = new CancellationTokenSource();
        var timeoutToken = timeoutCts.Token;
        lock (_gate)
        {
            if (_pending is not null || _active is not null) throw new InvalidOperationException("An audio session is already pending or active.");
            _pending = new PendingOffer(frame.MessageId, offer, receiver, timeoutCts);
        }
        DiagnosticLog.Current.Info("audio.offer-created", $"session={sessionId} message={frame.MessageId} udpPort={receiver.LocalPort} remote={_remoteAddress}");
        try
        {
            await _control.SendAsync(frame, cancellationToken).ConfigureAwait(false);
            DiagnosticLog.Current.Info("audio.offer-sent", $"session={sessionId} message={frame.MessageId}");
            _ = ExpireOfferAsync(frame.MessageId, timeoutToken);
        }
        catch
        {
            lock (_gate) _pending = null;
            timeoutCts.Dispose();
            await receiver.DisposeAsync();
            throw;
        }
    }

    public async Task AcceptAsync(AudioSessionOffer remoteOffer, Guid offerMessageId, CancellationToken cancellationToken)
    {
        DiagnosticLog.Current.Info("audio.offer-accepting", $"session={remoteOffer.SessionId} message={offerMessageId} remoteUdpPort={remoteOffer.UdpPort}");
        var material = AudioSessionKeyMaterial.Create();
        var localStreamId = Guid.NewGuid();
        var receiver = new UdpAudioReceiver(_localUdpPort, material.Key, material.NoncePrefix);
        var answer = new AudioSessionAnswer(remoteOffer.SessionId, localStreamId, checked((ushort)receiver.LocalPort), material.Key, material.NoncePrefix);
        UdpAudioSender? sender = null;
        AudioPipelineSession? session = null;
        try
        {
            await _control.SendAsync(answer.ToFrame(offerMessageId), cancellationToken).ConfigureAwait(false);
            DiagnosticLog.Current.Info("audio.answer-sent", $"session={remoteOffer.SessionId} correlation={offerMessageId} udpPort={receiver.LocalPort}");
            sender = new UdpAudioSender(new IPEndPoint(_remoteAddress, remoteOffer.UdpPort), remoteOffer.Key, remoteOffer.NoncePrefix);
            session = new AudioPipelineSession(remoteOffer.SessionId, localStreamId, remoteOffer.StreamId, _deviceFactory(), sender, receiver);
            lock (_gate) _active = session;
            SessionReady?.Invoke(session);
            DiagnosticLog.Current.Info("audio.session-ready", $"session={remoteOffer.SessionId} role=answerer");
        }
        catch
        {
            if (session is not null)
            {
                lock (_gate) { if (ReferenceEquals(_active, session)) _active = null; }
                await session.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                if (sender is not null) await sender.DisposeAsync().ConfigureAwait(false);
                await receiver.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    void OnFrame(ControlFrame frame)
    {
        if (frame.Type == ControlMessageType.AudioSessionOffer)
        {
            DiagnosticLog.Current.Info("audio.offer-received", $"message={frame.MessageId}");
            try { IncomingOffer?.Invoke(AudioSessionFrameCodec.DecodeOffer(frame), frame.MessageId); }
            catch (MalformedFrameException ex) { DiagnosticLog.Current.Warning("audio.offer-malformed", $"message={frame.MessageId}", ex); }
        }
        else if (frame.Type == ControlMessageType.AudioSessionAccepted)
        {
            DiagnosticLog.Current.Info("audio.answer-received", $"message={frame.MessageId} correlation={frame.CorrelationId}");
            _ = CompleteOfferAsync(frame);
        }
        else if (frame.Type == ControlMessageType.AudioSessionStopped)
        {
            _ = StopActiveAsync();
        }
    }

    async Task CompleteOfferAsync(ControlFrame frame)
    {
        PendingOffer? pending;
        lock (_gate)
        {
            pending = _pending;
            if (pending is null || frame.CorrelationId != pending.MessageId) return;
            _pending = null;
        }
        pending.TimeoutCts.Cancel();
        pending.TimeoutCts.Dispose();
        try
        {
            var answer = AudioSessionFrameCodec.DecodeAnswer(frame);
            if (answer.SessionId != pending.Offer.SessionId) throw new MalformedFrameException("Audio answer session mismatch.");
            var sender = new UdpAudioSender(new IPEndPoint(_remoteAddress, answer.UdpPort), answer.Key, answer.NoncePrefix);
            var session = new AudioPipelineSession(answer.SessionId, pending.Offer.StreamId, answer.StreamId, _deviceFactory(), sender, pending.Receiver);
            lock (_gate) _active = session;
            SessionReady?.Invoke(session);
            DiagnosticLog.Current.Info("audio.session-ready", $"session={answer.SessionId} role=offerer");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Current.Error("audio.answer-invalid", $"correlation={frame.CorrelationId}", ex);
            NegotiationFailed?.Invoke("The voice answer was invalid.");
            await pending.Receiver.DisposeAsync().ConfigureAwait(false);
        }
    }

    async Task ExpireOfferAsync(Guid messageId, CancellationToken cancellationToken)
    {
        try { await Task.Delay(_offerTimeout, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        PendingOffer? expired = null;
        lock (_gate)
        {
            if (_pending?.MessageId == messageId) { expired = _pending; _pending = null; }
        }
        if (expired is null) return;
        expired.TimeoutCts.Dispose();
        await expired.Receiver.DisposeAsync().ConfigureAwait(false);
        DiagnosticLog.Current.Warning("audio.offer-timeout", $"message={messageId} timeoutMs={_offerTimeout.TotalMilliseconds:0}");
        NegotiationFailed?.Invoke("The other PC did not answer the voice request.");
    }

    void OnConnectionDropped() => _ = StopActiveAsync();

    async Task StopActiveAsync()
    {
        AudioPipelineSession? session;
        lock (_gate) { session = _active; _active = null; }
        if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
    }

    public async Task RetireAsync(AudioPipelineSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            if (ReferenceEquals(_active, session)) _active = null;
        }
        await session.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _control.FrameReceived -= OnFrame;
        _control.ConnectionDropped -= OnConnectionDropped;
        PendingOffer? pending;
        lock (_gate) { pending = _pending; _pending = null; }
        if (pending is not null)
        {
            pending.TimeoutCts.Cancel();
            pending.TimeoutCts.Dispose();
            await pending.Receiver.DisposeAsync().ConfigureAwait(false);
        }
        await StopActiveAsync().ConfigureAwait(false);
    }

    sealed record PendingOffer(Guid MessageId, AudioSessionOffer Offer, UdpAudioReceiver Receiver, CancellationTokenSource TimeoutCts);
}
