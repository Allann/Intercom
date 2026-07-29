using System.Security.Cryptography.X509Certificates;
using System.Net;

namespace Intercom.ControlChannel;

/// <summary>
/// One live, TLS-authenticated transport connection to a peer — the seam
/// between <see cref="PeerControlChannel"/>'s policy (state machine, framing,
/// trust) and the real SslStream/TcpClient plumbing (<see cref="SslPeerConnection"/>),
/// mirroring how <c>Intercom.Discovery.IDnsServiceDiscovery</c> sits between
/// <c>DiscoveryService</c> and <c>Win32DnsServiceDiscovery</c>. Frame
/// send/receive only — everything about WHEN to send what and how to react
/// is <see cref="PeerControlChannel"/>'s job, not this seam's.
/// </summary>
public interface IPeerTransportConnection : IAsyncDisposable
{
    IPAddress RemoteAddress { get; }
    /// <summary>The certificate the remote side authenticated with during
    /// the TLS handshake. Always present — mutual TLS with a required client
    /// certificate means a connection that reaches this interface always has
    /// one on both sides.</summary>
    X509Certificate2 RemoteCertificate { get; }

    /// <summary>Sends one frame. Not safe to call concurrently from two
    /// callers at once — implementations serialize internally, but callers
    /// should still avoid relying on interleaving order across concurrent
    /// calls.</summary>
    Task SendAsync(ControlFrame frame, CancellationToken cancellationToken);

    /// <summary>Reads the next frame, or null if the peer closed the
    /// connection cleanly with nothing more to send. Throws
    /// <see cref="MalformedFrameException"/> on a bounds/framing violation
    /// (untrusted input) or a transport-level exception (IOException,
    /// OperationCanceledException, ObjectDisposedException) on a genuine
    /// connection failure — callers should treat any exception from this
    /// method the same way: the connection is dead, react accordingly (drop
    /// to Reconnecting), never retry the read on the same instance.</summary>
    Task<ControlFrame?> ReceiveAsync(CancellationToken cancellationToken);
}
