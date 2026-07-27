using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Intercom.ControlChannel;

/// <summary>
/// Dials an outbound mutually-authenticated TLS connection using real
/// TcpClient/SslStream. Chain/CA validation is deliberately bypassed in the
/// TLS-layer callback below (self-signed identities per ADR-0002 have no CA
/// to validate against, and a self-signed cert always fails default chain
/// validation) — trust is decided afterward at the application layer by
/// <see cref="PeerCertificateValidator"/> comparing the presented
/// certificate's SPKI hash against the approved-peer registry, not by the
/// TLS stack itself. This is what lets a not-yet-approved peer still
/// complete the handshake and land in restricted pairing-only mode
/// (ADR-0001), rather than being rejected before the application layer ever
/// sees it.
///
/// This class has not been runtime-validated against a real second peer in
/// this sandbox (no second machine available) — see the module-level report
/// for exactly what still needs a two-machine pass, mirroring how
/// <c>Win32DnsServiceDiscovery</c> was reviewed against documented API
/// behavior but not runtime-validated for issue #20.
/// </summary>
public sealed class SslPeerTransportConnector : IPeerTransportConnector
{
    readonly X509Certificate2 _localCertificate;

    public SslPeerTransportConnector(X509Certificate2 localCertificate)
    {
        _localCertificate = localCertificate;
    }

    public async Task<IPeerTransportConnection> ConnectAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        var tcpClient = new TcpClient(endpoint.AddressFamily);
        try
        {
            await tcpClient.ConnectAsync(endpoint.Address, endpoint.Port, cancellationToken).ConfigureAwait(false);

            var sslStream = new SslStream(
                tcpClient.GetStream(),
                leaveInnerStreamOpen: false,
                userCertificateValidationCallback: (_, _, _, _) => true);
            try
            {
                var options = new SslClientAuthenticationOptions
                {
                    // TargetHost has no DNS/hostname-validation meaning here
                    // (chain/hostname validation is bypassed entirely — see
                    // the class doc); it only needs to be a non-empty string
                    // to satisfy SslStream's API contract.
                    TargetHost = endpoint.Address.ToString(),
                    ClientCertificates = [_localCertificate],
                    ApplicationProtocols = [ControlChannelProtocol.AlpnId],
                    EnabledSslProtocols = SslProtocols.None, // OS-selected version, per issue #21 scope
                };
                await sslStream.AuthenticateAsClientAsync(options, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await sslStream.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            // sslStream now owns the underlying NetworkStream/socket;
            // SslPeerConnection.DisposeAsync disposing it is what ultimately
            // closes the TCP connection.
            return new SslPeerConnection(sslStream);
        }
        catch
        {
            tcpClient.Dispose();
            throw;
        }
    }
}
