using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Intercom.Audio;
using Xunit;

namespace Intercom.App.Tests.Audio;

public sealed class UdpAudioTransportTests
{
    [Fact]
    public async Task EncryptedDatagramCrossesRealLoopbackSocket()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        await using var receiver = new UdpAudioReceiver(0, key, 99);
        await using var sender = new UdpAudioSender(new IPEndPoint(IPAddress.IPv6Loopback, receiver.LocalPort), key, 99);
        var expected = new AudioPacket
        {
            SessionId = Guid.NewGuid(),
            StreamId = Guid.NewGuid(),
            Sequence = 1,
            SampleTimestamp = 960,
            Flags = AudioPacketFlags.None,
            OpusPayload = [4, 5, 6],
        };
        await sender.SendAsync(expected, CancellationToken.None);
        var actual = await receiver.ReceiveAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(actual);
        Assert.Equal(expected.Sequence, actual.Sequence);
        Assert.Equal(expected.OpusPayload, actual.OpusPayload);
    }

    [Fact]
    public async Task Ipv4MappedEndpointSendsToIpv4Receiver()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var receiverEndpoint = (IPEndPoint)receiver.Client.LocalEndPoint!;
        var mappedAddress = IPAddress.Parse($"::ffff:{receiverEndpoint.Address}");
        await using var sender = new UdpAudioSender(
            new IPEndPoint(mappedAddress, receiverEndpoint.Port),
            key,
            99);
        var expected = new AudioPacket
        {
            SessionId = Guid.NewGuid(),
            StreamId = Guid.NewGuid(),
            Sequence = 1,
            SampleTimestamp = 960,
            Flags = AudioPacketFlags.None,
            OpusPayload = [4, 5, 6],
        };

        await sender.SendAsync(expected, CancellationToken.None);

        var datagram = await receiver.ReceiveAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        using var protector = new AudioPacketProtector(key, 99);
        Assert.True(protector.TryUnprotect(datagram.Buffer, new AudioReplayWindow(), out var actual));
        Assert.NotNull(actual);
        Assert.Equal(expected.Sequence, actual.Sequence);
        Assert.Equal(expected.OpusPayload, actual.OpusPayload);
    }
}
