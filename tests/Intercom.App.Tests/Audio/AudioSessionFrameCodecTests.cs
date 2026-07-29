using System.Security.Cryptography;
using Intercom.Audio;
using Xunit;

namespace Intercom.App.Tests.Audio;

public sealed class AudioSessionFrameCodecTests
{
    [Fact]
    public void OfferRoundTripsFreshStreamMaterial()
    {
        var offer = new AudioSessionOffer(Guid.NewGuid(), Guid.NewGuid(), 49152, RandomNumberGenerator.GetBytes(32), 1234);
        var decoded = AudioSessionFrameCodec.DecodeOffer(offer.ToFrame(Guid.NewGuid()));
        Assert.Equal(offer.SessionId, decoded.SessionId);
        Assert.Equal(offer.StreamId, decoded.StreamId);
        Assert.Equal(offer.UdpPort, decoded.UdpPort);
        Assert.Equal(offer.Key, decoded.Key);
        Assert.Equal(offer.NoncePrefix, decoded.NoncePrefix);
    }

    [Fact]
    public void AnswerRoundTripsOppositeDirectionMaterialAndCorrelatesOffer()
    {
        var offerMessageId = Guid.NewGuid();
        var answer = new AudioSessionAnswer(Guid.NewGuid(), Guid.NewGuid(), 49153, RandomNumberGenerator.GetBytes(32), 5678);
        var frame = answer.ToFrame(offerMessageId);
        var decoded = AudioSessionFrameCodec.DecodeAnswer(frame);
        Assert.Equal(offerMessageId, frame.CorrelationId);
        Assert.Equal(answer.SessionId, decoded.SessionId);
        Assert.Equal(answer.StreamId, decoded.StreamId);
        Assert.Equal(answer.UdpPort, decoded.UdpPort);
        Assert.Equal(answer.Key, decoded.Key);
        Assert.Equal(answer.NoncePrefix, decoded.NoncePrefix);
    }
}
