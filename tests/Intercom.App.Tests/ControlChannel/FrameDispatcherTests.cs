using Intercom.ControlChannel;
using Xunit;

namespace Intercom.App.Tests.ControlChannel;

public class FrameDispatcherTests
{
    static ControlFrame HelloFrame() => Hello.Current(Capability.Text).ToFrame(Guid.NewGuid());

    static ControlFrame ApplicationFrame(ControlMessageType type = (ControlMessageType)100) => new()
    {
        Type = type,
        MessageId = Guid.NewGuid(),
        Payload = [1, 2, 3],
    };

    static ConnectionTrust Approved() => new ConnectionTrust.Approved { PeerId = Guid.NewGuid() };

    [Fact]
    public void FirstFrame_MustBeHello_NonHelloRejected()
    {
        var dispatcher = new FrameDispatcher(Approved());

        var accepted = dispatcher.Dispatch(ApplicationFrame());

        Assert.False(accepted);
    }

    [Fact]
    public void FirstFrame_Hello_IsAccepted()
    {
        var dispatcher = new FrameDispatcher(Approved());

        var accepted = dispatcher.Dispatch(HelloFrame());

        Assert.True(accepted);
    }

    [Fact]
    public void SecondHello_OnSameConnection_IsRejected()
    {
        var dispatcher = new FrameDispatcher(Approved());
        dispatcher.Dispatch(HelloFrame());

        var accepted = dispatcher.Dispatch(HelloFrame());

        Assert.False(accepted);
    }

    [Fact]
    public void AfterHello_ApplicationFrame_IsAcceptedOnApprovedConnection()
    {
        var dispatcher = new FrameDispatcher(Approved());
        dispatcher.Dispatch(HelloFrame());

        var accepted = dispatcher.Dispatch(ApplicationFrame());

        Assert.True(accepted);
    }

    [Fact]
    public void PairingOnly_RejectsApplicationFrame_EvenAfterHello()
    {
        var dispatcher = new FrameDispatcher(new ConnectionTrust.PairingOnly());
        dispatcher.Dispatch(HelloFrame());

        var accepted = dispatcher.Dispatch(ApplicationFrame());

        Assert.False(accepted);
    }

    [Fact]
    public void PairingOnly_StillAcceptsHello()
    {
        var dispatcher = new FrameDispatcher(new ConnectionTrust.PairingOnly());

        var accepted = dispatcher.Dispatch(HelloFrame());

        Assert.True(accepted);
    }

    [Fact]
    public void AcceptedApplicationFrame_GeneratesDeliveredReceipt_CorrelatedToItsMessageId()
    {
        var dispatcher = new FrameDispatcher(Approved());
        dispatcher.Dispatch(HelloFrame());
        var receipts = new List<ControlFrame>();
        dispatcher.DeliveredReceiptReady += receipts.Add;

        var appFrame = ApplicationFrame();
        dispatcher.Dispatch(appFrame);

        var receipt = Assert.Single(receipts);
        Assert.Equal(ControlMessageType.Delivered, receipt.Type);
        Assert.Equal(appFrame.MessageId, receipt.CorrelationId);
    }

    [Fact]
    public void Hello_DoesNotItselfGenerateADeliveredReceipt()
    {
        var dispatcher = new FrameDispatcher(Approved());
        var receipts = new List<ControlFrame>();
        dispatcher.DeliveredReceiptReady += receipts.Add;

        dispatcher.Dispatch(HelloFrame());

        Assert.Empty(receipts);
    }

    [Fact]
    public void DeliveredReceiptFrame_DoesNotItselfGenerateAnotherDeliveredReceipt()
    {
        var dispatcher = new FrameDispatcher(Approved());
        dispatcher.Dispatch(HelloFrame());
        var receipts = new List<ControlFrame>();
        dispatcher.DeliveredReceiptReady += receipts.Add;

        var deliveredFrame = new ControlFrame
        {
            Type = ControlMessageType.Delivered,
            MessageId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            Payload = [],
        };
        dispatcher.Dispatch(deliveredFrame);

        Assert.Empty(receipts);
    }

    [Fact]
    public void RejectedFrame_NeverRaisesFrameAcceptedOrDeliveredReceipt()
    {
        var dispatcher = new FrameDispatcher(new ConnectionTrust.PairingOnly());
        dispatcher.Dispatch(HelloFrame());
        var accepted = new List<ControlFrame>();
        var receipts = new List<ControlFrame>();
        dispatcher.FrameAccepted += accepted.Add;
        dispatcher.DeliveredReceiptReady += receipts.Add;

        dispatcher.Dispatch(ApplicationFrame());

        Assert.Empty(accepted);
        Assert.Empty(receipts);
    }
}
