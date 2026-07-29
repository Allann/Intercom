using System.Net;
using System.Net.Sockets;

namespace Intercom.Audio;

public sealed class UdpAudioSender : IAsyncDisposable
{
    readonly UdpClient _udp;
    readonly IPEndPoint _remote;
    readonly AudioPacketProtector _protector;

    public UdpAudioSender(IPEndPoint remote, ReadOnlySpan<byte> key, uint noncePrefix)
    {
        _remote = remote;
        _udp = new UdpClient(remote.AddressFamily);
        _protector = new AudioPacketProtector(key, noncePrefix);
    }

    public ValueTask<int> SendAsync(AudioPacket packet, CancellationToken cancellationToken) =>
        _udp.SendAsync(_protector.Protect(packet), _remote, cancellationToken);

    public ValueTask DisposeAsync()
    {
        _protector.Dispose();
        _udp.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class UdpAudioReceiver : IAsyncDisposable
{
    readonly UdpClient _udp;
    readonly AudioPacketProtector _protector;
    readonly AudioReplayWindow _replayWindow = new();

    public UdpAudioReceiver(int port, ReadOnlySpan<byte> key, uint noncePrefix)
    {
        _udp = new UdpClient(AddressFamily.InterNetworkV6);
        _udp.Client.DualMode = true;
        _udp.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
        _protector = new AudioPacketProtector(key, noncePrefix);
    }

    public int LocalPort => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

    public async ValueTask<AudioPacket?> ReceiveAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await _udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (_protector.TryUnprotect(result.Buffer, _replayWindow, out var packet)) return packet;
        }
        return null;
    }

    public ValueTask DisposeAsync()
    {
        _protector.Dispose();
        _udp.Dispose();
        return ValueTask.CompletedTask;
    }
}
