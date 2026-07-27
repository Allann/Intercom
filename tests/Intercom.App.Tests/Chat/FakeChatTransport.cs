using Intercom.ControlChannel;

namespace Intercom.App.Tests.Chat;

/// <summary>A hand-written in-memory <see cref="Intercom.Chat.IChatTransport"/>
/// fake, no mocking framework, mirroring <c>FakePairingTransport</c>'s role
/// for <c>IPairingTransport</c>.</summary>
sealed class FakeChatTransport : Intercom.Chat.IChatTransport
{
    public List<ControlFrame> Sent { get; } = [];
    public Exception? FailSendWith { get; set; }

    /// <summary>Test-only convenience for #30's fan-out routing tests: when
    /// set, every successful <see cref="SendAsync"/> synchronously raises
    /// <see cref="DeliveryConfirmed"/> for that frame's MessageId before
    /// returning — deterministic "delivered immediately," with no real delay
    /// or background task/race involved.</summary>
    public bool AutoConfirmDelivery { get; set; }

    public event Action<ControlFrame>? FrameReceived;
    public event Action<Guid>? DeliveryConfirmed;
    public event Action? ConnectionDropped;

    public Task SendAsync(ControlFrame frame, CancellationToken cancellationToken)
    {
        if (FailSendWith is not null) throw FailSendWith;
        Sent.Add(frame);
        if (AutoConfirmDelivery) DeliveryConfirmed?.Invoke(frame.MessageId);
        return Task.CompletedTask;
    }

    public void Deliver(ControlFrame frame) => FrameReceived?.Invoke(frame);
    public void ConfirmDelivery(Guid messageId) => DeliveryConfirmed?.Invoke(messageId);
    public void Drop() => ConnectionDropped?.Invoke();
}
