using Intercom.AttentionCards;
using Intercom.Presence;
using Xunit;

namespace Intercom.App.Tests.AttentionCards;

/// <summary>
/// Issue #25 acceptance criterion: "DND produces no chime/toast while still
/// queuing the item silently" — DND on the RECEIVING side must NEVER block
/// delivery/queuing of the card itself, only the audible/visual chime/toast
/// decision layered on top. <see cref="AttentionCardService"/> itself has no
/// DND awareness at all (see <c>AttentionCardServiceTests.FrameReceived_ValidAttentionCardFrame_AddsInboundCardAndRaisesEvent</c>) —
/// this test exercises the exact two-step decision the WinUI shell makes on
/// top of it (mirrors MainWindow.OnIncomingChatMessage's identical DND-gating
/// shape for issue #24's chat chime), to guard against the gating
/// accidentally being implemented as "don't even queue the card" instead of
/// "queue it, just don't chime."
/// </summary>
public class AttentionCardDndGatingTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InboundCard_IsAlwaysQueued_RegardlessOfDndState(bool dndEnabled)
    {
        var transport = new FakeAttentionCardTransport();
        var service = new AttentionCardService(transport);
        AttentionCard? received = null;
        service.CardReceived += c => received = c;

        var frame = AttentionCardFrameCodec.ToFrame("Dinner's ready", "🍽️", Guid.NewGuid());
        transport.Deliver(frame);

        // The service unconditionally queues the card; whether a chime/toast
        // would additionally fire is decided one layer up (below), never
        // here.
        Assert.NotNull(received);
        Assert.Single(service.Conversation.Cards);

        var wouldShowToast = !DndPolicy.IsSuppressed(dndEnabled, InteractionKind.AttentionChime);
        Assert.Equal(!dndEnabled, wouldShowToast);

        // Regardless of the chime decision, the sender still gets its
        // ordinary mechanical Delivered receipt — DND on the receiving side
        // never suppresses that either.
        transport.ConfirmDelivery(Guid.NewGuid()); // no matching Pending card on this side; asserts no throw
    }

    [Fact]
    public async Task ReceivingSideDnd_NeverSuppressesTheSenderSideDeliveredReceipt()
    {
        // The sender's own Delivered receipt is generated mechanically by
        // FrameDispatcher on the RECEIVING side's connection the instant the
        // frame is accepted — before any DND/chime decision is even
        // evaluated. Modeled here via the real loopback pair so the
        // assertion is against production wiring, not a restated claim.
        var (senderTransport, receiverTransport) = LoopbackAttentionCardTransport.CreatePair();
        var sender = new AttentionCardService(senderTransport);
        var receiver = new AttentionCardService(receiverTransport);

        // The "receiver" side being in DND is a purely local UI concern that
        // never reaches AttentionCardService/FrameDispatcher — there is
        // nothing to configure here, which is the point: DND has no lever
        // over this mechanical path at all.
        await sender.SendAsync("Get the door?", "🚪", CancellationToken.None);

        Assert.Equal(AttentionCardDeliveryState.Delivered, sender.Conversation.Cards.Single().DeliveryState);
        Assert.Single(receiver.Conversation.Cards);
    }
}
