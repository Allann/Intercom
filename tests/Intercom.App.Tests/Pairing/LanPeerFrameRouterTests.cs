using CsCheck;
using Intercom.ControlChannel;
using Intercom.Pairing;
using Xunit;

namespace Intercom.App.Tests.Pairing;

public sealed class LanPeerFrameRouterTests
{
    static readonly Guid ExpectedPeer = Guid.NewGuid();

    [Fact]
    public void Promotion_AcceptsAnEmptySlotOrTheSameConnection_AndRejectsACompetitor()
    {
        var candidate = new object();

        Assert.True(PairingConnectionPromotion.ShouldPromote(null, candidate));
        Assert.True(PairingConnectionPromotion.ShouldPromote(candidate, candidate));
        Assert.False(PairingConnectionPromotion.ShouldPromote(new object(), candidate));
        Assert.False(PairingConnectionPromotion.ShouldDispose(promoted: true));
        Assert.True(PairingConnectionPromotion.ShouldDispose(promoted: false));
    }

    [Theory]
    [InlineData(ControlMessageType.Chat)]
    [InlineData(ControlMessageType.ChatTyping)]
    [InlineData(ControlMessageType.ChatImageStart)]
    [InlineData(ControlMessageType.ChatImageChunk)]
    [InlineData(ControlMessageType.ChatImageComplete)]
    [InlineData(ControlMessageType.ChatImageReceived)]
    [InlineData(ControlMessageType.ChatImageCancelled)]
    [InlineData(ControlMessageType.ChatImageFailed)]
    public void ChatFrame_FromExpectedPeer_IsRouted(ControlMessageType type) =>
        Assert.True(LanPeerFrameRouter.IsChatFrame(ExpectedPeer, ExpectedPeer, type));

    [Theory]
    [InlineData(ControlMessageType.AudioSessionOffer)]
    [InlineData(ControlMessageType.AudioSessionAccepted)]
    [InlineData(ControlMessageType.AudioSessionStopped)]
    [InlineData(ControlMessageType.AudioSessionRejected)]
    public void AudioFrame_FromExpectedPeer_IsRouted(ControlMessageType type) =>
        Assert.True(LanPeerFrameRouter.IsAudioFrame(ExpectedPeer, ExpectedPeer, type));

    [Fact]
    public void ChatAndAudioFrames_RejectOtherTypesAndPeers()
    {
        Assert.False(LanPeerFrameRouter.IsChatFrame(ExpectedPeer, ExpectedPeer, ControlMessageType.Presence));
        Assert.False(LanPeerFrameRouter.IsChatFrame(ExpectedPeer, Guid.NewGuid(), ControlMessageType.Chat));
        Assert.False(LanPeerFrameRouter.IsAudioFrame(ExpectedPeer, ExpectedPeer, ControlMessageType.Chat));
        Assert.False(LanPeerFrameRouter.IsAudioFrame(ExpectedPeer, Guid.NewGuid(), ControlMessageType.AudioSessionOffer));
    }

    [Theory]
    [InlineData(ControlMessageType.AttentionCard, false, 1)]
    [InlineData(ControlMessageType.Acknowledged, true, 2)]
    [InlineData(ControlMessageType.Resolved, true, 3)]
    [InlineData(ControlMessageType.Acknowledged, false, 0)]
    [InlineData(ControlMessageType.Resolved, false, 0)]
    [InlineData(ControlMessageType.Chat, true, 0)]
    public void AttentionCardFrame_IsClassified(ControlMessageType type, bool correlated, int expected)
    {
        var frame = Frame(type, correlated ? Guid.NewGuid() : null);
        Assert.Equal((AttentionCardFrameRoute)expected, LanPeerFrameRouter.AttentionCardRoute(ExpectedPeer, ExpectedPeer, frame));
    }

    [Fact]
    public void AttentionCardFrame_FromAnotherPeer_IsIgnored() =>
        Assert.Equal(AttentionCardFrameRoute.None,
            LanPeerFrameRouter.AttentionCardRoute(ExpectedPeer, Guid.NewGuid(), Frame(ControlMessageType.AttentionCard)));

    [Fact]
    public void Forwarders_InvokeOnlyTheSelectedRuntimeEffect()
    {
        var chat = 0; var audio = 0; var card = 0; var acknowledged = 0; var resolved = 0; var group = 0;
        LanPeerFrameRouter.ForwardChat(ExpectedPeer, ExpectedPeer, Frame(ControlMessageType.Chat), _ => chat++);
        LanPeerFrameRouter.ForwardAudio(ExpectedPeer, ExpectedPeer, Frame(ControlMessageType.AudioSessionOffer), _ => audio++);
        LanPeerFrameRouter.ForwardAttentionCard(ExpectedPeer, ExpectedPeer, Frame(ControlMessageType.AttentionCard), _ => card++, _ => acknowledged++, _ => resolved++);
        LanPeerFrameRouter.ForwardAttentionCard(ExpectedPeer, ExpectedPeer, Frame(ControlMessageType.Acknowledged, Guid.NewGuid()), _ => card++, _ => acknowledged++, _ => resolved++);
        LanPeerFrameRouter.ForwardAttentionCard(ExpectedPeer, ExpectedPeer, Frame(ControlMessageType.Resolved, Guid.NewGuid()), _ => card++, _ => acknowledged++, _ => resolved++);
        LanPeerFrameRouter.ForwardGroupFloor(ExpectedPeer, Frame(ControlMessageType.GroupFloor), (_, _) => group++);
        LanPeerFrameRouter.ForwardGroupFloor(ExpectedPeer, Frame(ControlMessageType.Chat), (_, _) => group++);

        Assert.Equal((1, 1, 1, 1, 1, 1), (chat, audio, card, acknowledged, resolved, group));
    }

    [Fact]
    public void Forwarders_AcceptNoSubscriberAndIgnoreUnselectedFrames()
    {
        LanPeerFrameRouter.ForwardChat(ExpectedPeer, ExpectedPeer, Frame(ControlMessageType.Chat), null);
        LanPeerFrameRouter.ForwardAudio(ExpectedPeer, ExpectedPeer, Frame(ControlMessageType.AudioSessionOffer), null);
        LanPeerFrameRouter.ForwardAttentionCard(ExpectedPeer, Guid.NewGuid(), Frame(ControlMessageType.AttentionCard), null, null, null);
    }

    [Fact]
    public void PeerValueAndSignal_ForwardOnlyForExpectedPeer()
    {
        var value = Guid.Empty; var signals = 0; var expectedValue = Guid.NewGuid();
        LanPeerFrameRouter.ForwardPeerValue(ExpectedPeer, ExpectedPeer, expectedValue, received => value = received);
        LanPeerFrameRouter.ForwardPeerValue(ExpectedPeer, Guid.NewGuid(), Guid.NewGuid(), received => value = received);
        LanPeerFrameRouter.ForwardPeerSignal(ExpectedPeer, ExpectedPeer, () => signals++);
        LanPeerFrameRouter.ForwardPeerSignal(ExpectedPeer, Guid.NewGuid(), () => signals++);
        LanPeerFrameRouter.ForwardPeerValue(ExpectedPeer, ExpectedPeer, expectedValue, null);
        LanPeerFrameRouter.ForwardPeerSignal(ExpectedPeer, ExpectedPeer, null);

        Assert.Equal(expectedValue, value);
        Assert.Equal(1, signals);
    }

    [Fact]
    public void PeerSignal_DoesNotTradeSelectedAndUnselectedEffects()
    {
        var selected = 0;
        var unselected = 0;

        LanPeerFrameRouter.ForwardPeerSignal(ExpectedPeer, ExpectedPeer, () => selected++);
        LanPeerFrameRouter.ForwardPeerSignal(ExpectedPeer, Guid.NewGuid(), () => unselected++);

        Assert.Equal(1, selected);
        Assert.Equal(0, unselected);
    }

    [Fact]
    public void GroupFloor_DoesNotTradeFloorAndNonFloorEffects()
    {
        var floor = 0;
        var chat = 0;

        LanPeerFrameRouter.ForwardGroupFloor(ExpectedPeer, Frame(ControlMessageType.GroupFloor), (_, _) => floor++);
        LanPeerFrameRouter.ForwardGroupFloor(ExpectedPeer, Frame(ControlMessageType.Chat), (_, _) => chat++);

        Assert.Equal(1, floor);
        Assert.Equal(0, chat);
    }

    [Fact]
    public void FramesFromAnyOtherPeerAreNeverRouted()
    {
        Gen.Guid.Sample(candidate =>
        {
            var otherPeer = candidate == ExpectedPeer ? Guid.Empty : candidate;
            foreach (var type in Enum.GetValues<ControlMessageType>())
            {
                Assert.False(LanPeerFrameRouter.IsChatFrame(ExpectedPeer, otherPeer, type));
                Assert.False(LanPeerFrameRouter.IsAudioFrame(ExpectedPeer, otherPeer, type));
                Assert.Equal(AttentionCardFrameRoute.None,
                    LanPeerFrameRouter.AttentionCardRoute(ExpectedPeer, otherPeer, Frame(type, Guid.NewGuid())));
            }
        });
    }

    static ControlFrame Frame(ControlMessageType type, Guid? correlationId = null) => new()
    {
        Type = type,
        MessageId = Guid.NewGuid(),
        CorrelationId = correlationId,
        Payload = [],
    };
}
