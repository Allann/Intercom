using System.Net;
using System.Net.Sockets;
using System.Text;
using Intercom.Diagnostics;
using Intercom.Identity;

namespace Intercom.Discovery;

/// <summary>Small explicit LAN liveness probe used when Windows DNS-SD fails
/// to return services that were already running. Trust never comes from this
/// datagram; callers must authenticate the subsequent TLS connection.</summary>
public sealed class LanDiscoveryProbe : IDisposable
{
    public const int Port = 47810;
    const string Prefix = "INTERCOM-DISCOVERY/1";
    readonly UdpClient _udp;
    readonly Guid _peerId;
    readonly int _controlPort;
    readonly SpkiPin _spki;
    readonly CancellationTokenSource _cts = new();
    Task? _receiveLoop;
    Timer? _announceTimer;

    public event Action<Guid, SpkiPin, IPEndPoint>? PeerAnswered;

    public LanDiscoveryProbe(Guid peerId, SpkiPin spki, int controlPort)
    {
        _peerId = peerId;
        _spki = spki;
        _controlPort = controlPort;
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.ExclusiveAddressUse = false;
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
        _udp.EnableBroadcast = true;
    }

    public void Start()
    {
        if (_receiveLoop is not null) throw new InvalidOperationException("LAN discovery probe already started.");
        _receiveLoop = ReceiveAsync(_cts.Token);
        Send("QUERY");
        Send(Advertisement());
        _announceTimer = new Timer(_ => Send(Advertisement()), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        DiagnosticLog.Current.Info("discovery.probe-started", $"udpPort={Port}");
    }

    async Task ReceiveAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var received = await _udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                var text = Encoding.UTF8.GetString(received.Buffer);
                if (text == $"{Prefix}|QUERY") { Send(Advertisement()); continue; }
                var parts = text.Split('|');
                if (parts.Length != 5 || parts[0] != Prefix || parts[1] != "HERE" ||
                    !Guid.TryParseExact(parts[2], "N", out var peerId) || peerId == _peerId ||
                    !int.TryParse(parts[3], out var port) || port is < 1 or > 65535) continue;
                SpkiPin spki;
                try { spki = new SpkiPin(Convert.FromHexString(parts[4])); }
                catch (Exception ex) when (ex is FormatException or ArgumentException) { continue; }
                var endpoint = new IPEndPoint(received.RemoteEndPoint.Address, port);
                DiagnosticLog.Current.Info("discovery.probe-answer", $"peer={peerId} endpoint={endpoint}");
                PeerAnswered?.Invoke(peerId, spki, endpoint);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex) { DiagnosticLog.Current.Error("discovery.probe-receive-failed", "UDP discovery receive failed.", ex); }
        }
    }

    string Advertisement() => $"HERE|{_peerId:N}|{_controlPort}|{_spki}";

    void Send(string payload)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes($"{Prefix}|{payload}");
            _udp.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, Port));
        }
        catch (Exception ex) when (!_cts.IsCancellationRequested)
        {
            DiagnosticLog.Current.Error("discovery.probe-send-failed", "UDP discovery broadcast failed.", ex);
        }
    }

    public void Dispose()
    {
        _announceTimer?.Dispose();
        _cts.Cancel();
        _udp.Dispose();
        _cts.Dispose();
    }
}
