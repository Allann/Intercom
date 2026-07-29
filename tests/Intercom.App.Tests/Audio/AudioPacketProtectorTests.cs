using System.Security.Cryptography;
using Intercom.Audio;
using Xunit;

namespace Intercom.App.Tests.Audio;

public sealed class AudioPacketProtectorTests
{
    static AudioPacket Packet(ulong sequence) => new()
    {
        SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        StreamId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Sequence = sequence,
        SampleTimestamp = sequence * 960,
        Flags = AudioPacketFlags.None,
        OpusPayload = [1, 2, 3, 4],
    };

    [Fact]
    public void RoundTrip_AuthenticatesHeaderAndPayload()
    {
        using var protector = new AudioPacketProtector(RandomNumberGenerator.GetBytes(32), 42);
        var replay = new AudioReplayWindow();
        Assert.True(protector.TryUnprotect(protector.Protect(Packet(7)), replay, out var decoded));
        Assert.NotNull(decoded);
        Assert.Equal(Packet(7).SessionId, decoded.SessionId);
        Assert.Equal(Packet(7).StreamId, decoded.StreamId);
        Assert.Equal(Packet(7).Sequence, decoded.Sequence);
        Assert.Equal(Packet(7).SampleTimestamp, decoded.SampleTimestamp);
        Assert.Equal(Packet(7).OpusPayload, decoded.OpusPayload);
    }

    [Fact]
    public void ModifiedDuplicateAndTooOldPackets_AreRejected()
    {
        using var protector = new AudioPacketProtector(RandomNumberGenerator.GetBytes(32), 42);
        var replay = new AudioReplayWindow();
        var valid = protector.Protect(Packet(100));
        var modified = valid.ToArray();
        modified[20] ^= 1;

        Assert.False(protector.TryUnprotect(modified, replay, out _));
        Assert.True(protector.TryUnprotect(valid, replay, out _));
        Assert.False(protector.TryUnprotect(valid, replay, out _));
        Assert.True(protector.TryUnprotect(protector.Protect(Packet(200)), replay, out _));
        Assert.False(protector.TryUnprotect(protector.Protect(Packet(120)), replay, out _));
    }

    [Fact]
    public void ReorderedPacketInsideWindow_IsAcceptedOnlyOnce()
    {
        using var protector = new AudioPacketProtector(RandomNumberGenerator.GetBytes(32), 42);
        var replay = new AudioReplayWindow();
        Assert.True(protector.TryUnprotect(protector.Protect(Packet(10)), replay, out _));
        Assert.True(protector.TryUnprotect(protector.Protect(Packet(12)), replay, out _));
        var eleven = protector.Protect(Packet(11));
        Assert.True(protector.TryUnprotect(eleven, replay, out _));
        Assert.False(protector.TryUnprotect(eleven, replay, out _));
    }
}
