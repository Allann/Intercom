using Intercom.ControlChannel;

namespace Intercom.App.Tests.AttentionCards;

/// <summary>A hand-written in-memory <see cref="Intercom.AttentionCards.IAttentionCardTransport"/>
/// fake, no mocking framework, mirroring <c>FakeChatTransport</c>'s role for
/// <c>IChatTransport</c>.</summary>
sealed class FakeAttentionCardTransport : Intercom.AttentionCards.IAttentionCardTransport
{
    public List<ControlFrame> Sent { get; } = [];
    public Exception? FailSendWith { get; set; }

    public event Action<ControlFrame>? FrameReceived;
    public event Action<Guid>? DeliveryConfirmed;
    public event Action<Guid>? Acknowledged;
    public event Action? ConnectionDropped;

    public Task SendAsync(ControlFrame frame, CancellationToken cancellationToken)
    {
        if (FailSendWith is not null) throw FailSendWith;
        Sent.Add(frame);
        return Task.CompletedTask;
    }

    public void Deliver(ControlFrame frame) => FrameReceived?.Invoke(frame);
    public void ConfirmDelivery(Guid messageId) => DeliveryConfirmed?.Invoke(messageId);
    public void ConfirmAcknowledged(Guid messageId) => Acknowledged?.Invoke(messageId);
    public void Drop() => ConnectionDropped?.Invoke();
}
