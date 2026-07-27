using Intercom.ControlChannel;

namespace Intercom.App.Tests.AttentionCards;

/// <summary>A hand-written in-memory <see cref="Intercom.AttentionCards.IAttentionCardTransport"/>
/// fake, no mocking framework, mirroring <c>FakeChatTransport</c>'s role for
/// <c>IChatTransport</c>.</summary>
sealed class FakeAttentionCardTransport : Intercom.AttentionCards.IAttentionCardTransport
{
    public List<ControlFrame> Sent { get; } = [];
    public Exception? FailSendWith { get; set; }

    /// <summary>Test-only convenience for #30's fan-out routing tests — see
    /// <c>FakeChatTransport.AutoConfirmDelivery</c>'s identical doc.</summary>
    public bool AutoConfirmDelivery { get; set; }

    /// <summary>Test-only convenience for #30's fan-out routing tests: when
    /// set, every successful <see cref="SendAsync"/> for a real
    /// AttentionCard frame (not a Resolved/Acknowledged/Delivered
    /// meta-frame) synchronously raises <see cref="Acknowledged"/> for that
    /// frame's MessageId before returning — used to deterministically
    /// simulate a peer's human acking faster than a still-in-progress
    /// fan-out loop can finish sending to every device.</summary>
    public bool AutoAcknowledge { get; set; }

    public event Action<ControlFrame>? FrameReceived;
    public event Action<Guid>? DeliveryConfirmed;
    public event Action<Guid>? Acknowledged;
    public event Action<Guid>? Resolved;
    public event Action? ConnectionDropped;

    public Task SendAsync(ControlFrame frame, CancellationToken cancellationToken)
    {
        if (FailSendWith is not null) throw FailSendWith;
        Sent.Add(frame);
        if (AutoConfirmDelivery) DeliveryConfirmed?.Invoke(frame.MessageId);
        if (AutoAcknowledge && frame.Type == ControlMessageType.AttentionCard) Acknowledged?.Invoke(frame.MessageId);
        return Task.CompletedTask;
    }

    public void Deliver(ControlFrame frame) => FrameReceived?.Invoke(frame);
    public void ConfirmDelivery(Guid messageId) => DeliveryConfirmed?.Invoke(messageId);
    public void ConfirmAcknowledged(Guid messageId) => Acknowledged?.Invoke(messageId);
    public void ConfirmResolved(Guid interactionId) => Resolved?.Invoke(interactionId);
    public void Drop() => ConnectionDropped?.Invoke();
}
