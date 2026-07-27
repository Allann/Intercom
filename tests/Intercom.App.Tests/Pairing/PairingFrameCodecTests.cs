using Intercom.ControlChannel;
using Intercom.Pairing;
using Xunit;

namespace Intercom.App.Tests.Pairing;

public class PairingFrameCodecTests
{
    [Fact]
    public void NonceMessage_RoundTripsThroughFrame()
    {
        var message = new PairingNonceMessage
        {
            Nonce = PairingNonce.Generate(),
            PeerId = Guid.NewGuid(),
            ProtocolVersion = Hello.CurrentProtocolVersion,
            Intent = PairingIntent.Pair,
        };

        var frame = message.ToFrame(Guid.NewGuid());
        var decoded = PairingFrameCodec.DecodeNonce(frame);

        Assert.Equal(ControlMessageType.PairingNonce, frame.Type);
        Assert.Equal(message.Nonce, decoded.Nonce);
        Assert.Equal(message.PeerId, decoded.PeerId);
        Assert.Equal(message.ProtocolVersion, decoded.ProtocolVersion);
        Assert.Equal(message.Intent, decoded.Intent);
    }

    [Fact]
    public void DecodeNonce_WrongFrameType_Throws()
    {
        var frame = PairingFrameCodec.ConfirmFrame(Guid.NewGuid());

        Assert.Throws<ArgumentException>(() => PairingFrameCodec.DecodeNonce(frame));
    }

    [Fact]
    public void DecodeNonce_TruncatedPayload_ThrowsMalformedFrame()
    {
        var frame = new ControlFrame { Type = ControlMessageType.PairingNonce, MessageId = Guid.NewGuid(), Payload = [1, 2, 3] };

        Assert.Throws<MalformedFrameException>(() => PairingFrameCodec.DecodeNonce(frame));
    }

    [Fact]
    public void ConfirmFrame_RejectFrame_ForgottenFrame_HaveEmptyPayloads_AndCorrectTypes()
    {
        var confirm = PairingFrameCodec.ConfirmFrame(Guid.NewGuid());
        var reject = PairingFrameCodec.RejectFrame(Guid.NewGuid());
        var forgotten = PairingFrameCodec.ForgottenFrame(Guid.NewGuid());

        Assert.Equal(ControlMessageType.PairingConfirm, confirm.Type);
        Assert.Empty(confirm.Payload);
        Assert.Equal(ControlMessageType.PairingReject, reject.Type);
        Assert.Empty(reject.Payload);
        Assert.Equal(ControlMessageType.Forgotten, forgotten.Type);
        Assert.Empty(forgotten.Payload);
    }

    [Fact]
    public void FrameDispatcher_PairingOnly_PermitsPairingMessageTypes_AfterHello()
    {
        var dispatcher = new FrameDispatcher(new ConnectionTrust.PairingOnly());
        dispatcher.Dispatch(Hello.Current(Capability.Text).ToFrame(Guid.NewGuid()));

        var nonceAccepted = dispatcher.Dispatch(new PairingNonceMessage
        {
            Nonce = PairingNonce.Generate(),
            PeerId = Guid.NewGuid(),
            ProtocolVersion = Hello.CurrentProtocolVersion,
            Intent = PairingIntent.Pair,
        }.ToFrame(Guid.NewGuid()));
        var confirmAccepted = dispatcher.Dispatch(PairingFrameCodec.ConfirmFrame(Guid.NewGuid()));
        var rejectAccepted = dispatcher.Dispatch(PairingFrameCodec.RejectFrame(Guid.NewGuid()));
        var forgottenAccepted = dispatcher.Dispatch(PairingFrameCodec.ForgottenFrame(Guid.NewGuid()));

        Assert.True(nonceAccepted);
        Assert.True(confirmAccepted);
        Assert.True(rejectAccepted);
        // Forgotten deliberately stays rejected on a PairingOnly connection —
        // it only ever makes sense once a peer is already approved.
        Assert.False(forgottenAccepted);
    }
}
