using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Intercom.Diagnostics;

namespace Intercom.ControlChannel;

/// <summary>
/// Accepts inbound mutually-authenticated TLS connections using real
/// TcpListener/SslStream, dual-stack (one socket serving both IPv4 and IPv6
/// clients, matching Intercom.Discovery's dual-stack support). See
/// <see cref="SslPeerTransportConnector"/>'s doc for why chain/CA validation
/// is bypassed at the TLS layer here too — trust is an application-layer
/// decision (<see cref="PeerCertificateValidator"/>), not a TLS-stack one.
///
/// Same untestability note as <see cref="SslPeerConnection"/>: this class
/// requires a live remote TLS client to exercise and has not been runtime-
/// validated against a real second peer in this sandbox.
/// </summary>
public sealed class SslPeerTransportListener : IPeerTransportListener
{
    readonly X509Certificate2 _localCertificate;
    readonly int _port;
    readonly CancellationTokenSource _cts = new();

    TcpListener? _listener;
    bool _disposed;

    public event Action<IPeerTransportConnection>? ConnectionAccepted;

    public SslPeerTransportListener(X509Certificate2 localCertificate, int port)
    {
        _localCertificate = localCertificate;
        _port = port;
    }

    public void Start()
    {
        if (_listener is not null) throw new InvalidOperationException("SslPeerTransportListener.Start must only be called once.");

        _listener = new TcpListener(IPAddress.IPv6Any, _port);
        _listener.Server.DualMode = true;
        _listener.Start();

        _ = AcceptLoopAsync(_cts.Token);
    }

    async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return; // shutting down
            }
            catch (ObjectDisposedException)
            {
                return; // listener stopped during shutdown
            }

            // Each inbound handshake runs independently so one slow or
            // hostile peer stalling its TLS handshake can't stall accepting
            // the next connection.
            _ = HandleInboundAsync(client, cancellationToken);
        }
    }

    async Task HandleInboundAsync(TcpClient client, CancellationToken cancellationToken)
    {
        SslStream? sslStream = null;
        try
        {
            sslStream = new SslStream(
                client.GetStream(),
                leaveInnerStreamOpen: false,
                userCertificateValidationCallback: (_, _, _, _) => true);

            var options = new SslServerAuthenticationOptions
            {
                ServerCertificate = _localCertificate,
                ClientCertificateRequired = true,
                ApplicationProtocols = [ControlChannelProtocol.AlpnId],
                EnabledSslProtocols = SslProtocols.None,
            };
            await sslStream.AuthenticateAsServerAsync(options, cancellationToken).ConfigureAwait(false);

            ConnectionAccepted?.Invoke(new SslPeerConnection(sslStream));
        }
        catch (Exception ex)
        {
            // Handshake failure (no client cert offered, ALPN mismatch,
            // cancelled during shutdown, etc.): nothing to hand the caller —
            // this never surfaces as ConnectionAccepted. Just clean up.
            DiagnosticLog.Current.Error("control-channel.inbound-tls-failed", "Inbound mutual-TLS handshake failed.", ex);
            if (sslStream is not null)
            {
                await sslStream.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                client.Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _listener?.Stop();
        _cts.Dispose();
    }
}
