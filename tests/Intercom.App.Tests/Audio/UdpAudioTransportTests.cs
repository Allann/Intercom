using System.Net;
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
}
