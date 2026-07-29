using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Net;

namespace Intercom.ControlChannel;

/// <summary>
/// Real <see cref="IPeerTransportConnection"/> over an already mutually-
/// authenticated <see cref="SslStream"/>. This class cannot be exercised by
/// an automated test in this sandbox — it requires a live second TLS peer —
/// mirroring <c>Win32DnsServiceDiscovery</c>'s documented untestability; the
/// policy it's built on (<see cref="FrameCodec"/>, <see cref="FrameDispatcher"/>,
/// <see cref="PeerConnectionStateMachine"/>) is what carries this module's
/// real test coverage.
/// </summary>
public sealed class SslPeerConnection : IPeerTransportConnection
{
    readonly SslStream _stream;
    public IPAddress RemoteAddress { get; }

    // SslStream has no internal serialization for concurrent writers — a
    // heartbeat-timer tick and an app-triggered send racing each other must
    // not interleave partial writes onto the wire. A SemaphoreSlim rather
    // than `lock` because the guarded body spans an actual async I/O call
    // (`await` inside a `lock` block is a compile error, and blocking a
    // thread-pool thread on a sync lock around network I/O would be worse).
    readonly SemaphoreSlim _writeLock = new(1, 1);

    bool _disposed;

    public SslPeerConnection(SslStream stream, IPAddress remoteAddress)
    {
        _stream = stream;
        RemoteAddress = remoteAddress;
    }

    public X509Certificate2 RemoteCertificate =>
        _stream.RemoteCertificate as X509Certificate2
            ?? throw new InvalidOperationException("Connection has no remote certificate — mutual TLS did not complete.");

    public async Task SendAsync(ControlFrame frame, CancellationToken cancellationToken)
    {
        var bytes = FrameCodec.Encode(frame);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<ControlFrame?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var header = new byte[FrameCodec.HeaderSize];

        // Read exactly 1 byte first so a clean close before any new frame
        // (0 bytes ever arrive) can be told apart from a connection that
        // dies mid-header (which ReadExactlyAsync below reports as
        // EndOfStreamException, correctly treated by the caller as a
        // transport failure rather than a graceful close).
        var firstByteCount = await _stream.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (firstByteCount == 0) return null;

        await _stream.ReadExactlyAsync(header.AsMemory(1), cancellationToken).ConfigureAwait(false);
        var frameHeader = FrameCodec.DecodeHeader(header);

        var payload = frameHeader.PayloadLength == 0 ? [] : new byte[frameHeader.PayloadLength];
        if (payload.Length > 0)
        {
            await _stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        }

        return FrameCodec.ToFrame(frameHeader, payload);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _writeLock.Dispose();
        await _stream.DisposeAsync().ConfigureAwait(false);
    }
}
