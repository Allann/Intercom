namespace Intercom.ControlChannel;

/// <summary>
/// Enforces per-connection frame acceptance rules and produces the
/// mechanical Delivered receipt (ADR-0001) — pure policy, no I/O, so it is
/// fully unit-testable. One instance owns exactly one live connection's
/// dispatch state (specifically: whether Hello has been received yet).
///
/// Two rules, both fail-closed:
/// <list type="bullet">
/// <item>Hello must be the first frame received, and the only one ever
/// accepted twice — everything else is rejected until Hello has been seen
/// once.</item>
/// <item>A <see cref="ConnectionTrust.PairingOnly"/> connection rejects every
/// message type except whatever pairing needs — Hello plus the pairing
/// ceremony messages added in issue #22 (see
/// <see cref="IsPairingPermitted"/>).</item>
/// </list>
/// </summary>
public sealed class FrameDispatcher
{
    readonly ConnectionTrust _trust;
    bool _helloReceived;

    public FrameDispatcher(ConnectionTrust trust)
    {
        _trust = trust;
    }

    /// <summary>Raised for every frame this dispatcher accepts (including
    /// Hello itself) once the ordering/trust rules pass.</summary>
    public event Action<ControlFrame>? FrameAccepted;

    /// <summary>Raised with the Delivered receipt to send back to the
    /// sender, the instant an accepted frame carries a message ID that
    /// warrants one — mechanical, no human involved, distinct from
    /// attention-card Acknowledged (issue #25). The caller is responsible
    /// for actually writing this frame back over the connection.</summary>
    public event Action<ControlFrame>? DeliveredReceiptReady;

    /// <summary>Applies the acceptance rules to one inbound frame. Returns
    /// true if it was accepted (raising <see cref="FrameAccepted"/> and,
    /// unless the frame is itself protocol meta-traffic, <see
    /// cref="DeliveredReceiptReady"/>), false if it was rejected. A rejected
    /// frame raises neither event — callers should treat repeated rejections
    /// as grounds to close the connection, since a well-behaved peer never
    /// sends a frame this dispatcher will reject.</summary>
    public bool Dispatch(ControlFrame frame)
    {
        if (!_helloReceived)
        {
            if (frame.Type != ControlMessageType.Hello) return false;
            _helloReceived = true;
        }
        else if (frame.Type == ControlMessageType.Hello)
        {
            // Hello is one-time; a second Hello on the same connection is a
            // protocol violation, not a re-negotiation.
            return false;
        }

        if (_trust is ConnectionTrust.PairingOnly && !IsPairingPermitted(frame.Type))
        {
            return false;
        }

        FrameAccepted?.Invoke(frame);

        // Hello and Delivered are protocol meta-messages, not themselves
        // something to deliver-acknowledge: a Delivered-for-Delivered would
        // just be self-referential noise, and Hello's own "delivery" is
        // signaled by the connection becoming usable at all.
        if (frame.Type is not (ControlMessageType.Hello or ControlMessageType.Delivered))
        {
            DeliveredReceiptReady?.Invoke(new ControlFrame
            {
                Type = ControlMessageType.Delivered,
                MessageId = Guid.NewGuid(),
                CorrelationId = frame.MessageId,
                Payload = [],
            });
        }

        return true;
    }

    static bool IsPairingPermitted(ControlMessageType type) => type is ControlMessageType.Hello
        or ControlMessageType.PairingNonce or ControlMessageType.PairingConfirm or ControlMessageType.PairingReject;
}
