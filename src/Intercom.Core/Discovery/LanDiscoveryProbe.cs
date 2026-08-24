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

    internal Action<string>? SendOverride { get; init; }

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
                ProcessReceived(received.Buffer, received.RemoteEndPoint);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex) { DiagnosticLog.Current.Error("discovery.probe-receive-failed", "UDP discovery receive failed.", ex); }
        }
    }

    internal void ProcessReceived(byte[] buffer, IPEndPoint remoteEndPoint)
    {
        var text = Encoding.UTF8.GetString(buffer);
        if (text == $"{Prefix}|QUERY") { Send(Advertisement()); return; }
        if (!TryParseAdvertisement(text, _peerId, out var advertisement)) return;

        var endpoint = new IPEndPoint(remoteEndPoint.Address, advertisement.Port);
        DiagnosticLog.Current.Info("discovery.probe-answer", $"peer={advertisement.PeerId} endpoint={endpoint}");
        PeerAnswered?.Invoke(advertisement.PeerId, advertisement.Spki, endpoint);
    }

    static bool TryParseAdvertisement(string text, Guid localPeerId, out ParsedAdvertisement advertisement)
    {
        advertisement = default;
        var parts = text.Split('|');
        if (!HasAdvertisementEnvelope(parts)) return false;
        if (!TryParseRemotePeer(parts[2], localPeerId, out var peerId)) return false;
        if (!TryParsePort(parts[3], out var port)) return false;
        if (!TryParseSpki(parts[4], out var spki)) return false;
        advertisement = new ParsedAdvertisement(peerId, port, spki);
        return true;
    }

    static bool HasAdvertisementEnvelope(string[] parts) =>
        parts.Length == 5 && parts[0] == Prefix && parts[1] == "HERE";

    static bool TryParseRemotePeer(string value, Guid localPeerId, out Guid peerId) =>
        Guid.TryParseExact(value, "N", out peerId) && peerId != localPeerId;

    static bool TryParsePort(string value, out int port) =>
        int.TryParse(value, out port) && port is >= 1 and <= 65535;

    static bool TryParseSpki(string value, out SpkiPin spki)
    {
        try { spki = new SpkiPin(Convert.FromHexString(value)); return true; }
        catch (Exception ex) when (ex is FormatException or ArgumentException) { spki = default; return false; }
    }

    readonly record struct ParsedAdvertisement(Guid PeerId, int Port, SpkiPin Spki);

    string Advertisement() => $"HERE|{_peerId:N}|{_controlPort}|{_spki}";

    void Send(string payload)
    {
        try
        {
            var datagram = $"{Prefix}|{payload}";
            if (SendOverride is not null) { SendOverride(datagram); return; }
            var bytes = Encoding.UTF8.GetBytes(datagram);
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
