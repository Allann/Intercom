using System.Net;

namespace Intercom.ControlChannel;

/// <summary>
/// Dials an outbound mutually-authenticated TLS connection to a peer's
/// discovered endpoint. See <see cref="IPeerTransportConnection"/>'s doc for
/// why this is a seam rather than <see cref="PeerControlChannel"/> using
/// TcpClient/SslStream directly.
/// </summary>
public interface IPeerTransportConnector
{
    /// <summary>Connects and completes the mutual TLS handshake. Throws on
    /// any failure (connect refused/timed out, TLS handshake failure,
    /// cancellation) — there is nothing partial to return.</summary>
    Task<IPeerTransportConnection> ConnectAsync(IPEndPoint endpoint, CancellationToken cancellationToken);
}
