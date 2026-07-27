using Intercom.ControlChannel;

namespace Intercom.Presence;

/// <summary>
/// Wires inbound <see cref="ControlMessageType.Presence"/> frames from one or
/// more live <see cref="PeerControlChannel"/> connections into a shared
/// <see cref="PresenceReceiver"/> (issue #30 — the receive side that
/// <see cref="PresenceLease"/>'s and <see cref="PresenceFrameCodec"/>'s own
/// doc comments call out as this ticket's job). Mirrors
/// <see cref="Chat.ChatService"/>'s "decode, drop malformed, hand to the pure
/// policy class" shape — this is the transport-crossing glue
/// <see cref="PresenceReceiver"/> deliberately does not know about.
///
/// One instance can track presence from many peers/devices at once — unlike
/// <see cref="Chat.ChatService"/>'s one-transport-per-conversation shape,
/// presence routing (#30) inherently needs a multi-device view, so
/// <see cref="Attach"/> can be called once per live
/// <see cref="PeerControlChannel"/> in the app's connection roster.
///
/// No live multi-peer connection roster exists in this app shell yet
/// (App.xaml.cs's <c>StartPresence</c> doc comment explains why in detail) —
/// this class is real, tested glue, exercised in tests against real
/// <see cref="PeerControlChannel"/>/fake transport pairs, not wired into
/// App.xaml.cs today. Wiring it in, once a live connection roster exists, is
/// exactly the same small mechanical addition StartPresence's doc comment
/// already describes for the SEND side.
/// </summary>
public sealed class PresenceReceiverService
{
    readonly Func<DateTimeOffset> _clock;

    public PresenceReceiverService(PresenceReceiver? receiver = null, Func<DateTimeOffset>? clock = null)
    {
        Receiver = receiver ?? new PresenceReceiver();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public PresenceReceiver Receiver { get; }

    /// <summary>Subscribes to one live channel's inbound Presence frames.
    /// Safe to call once per channel in a roster. A thin, deliberately
    /// one-line wiring around <see cref="HandleInboundFrame"/> — that method
    /// (public specifically so it is directly unit-testable without needing
    /// a live, fully-handshaken <see cref="PeerControlChannel"/>) carries all
    /// of the actual decode/ingest logic.</summary>
    public void Attach(PeerControlChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        channel.MessageReceived += HandleInboundFrame;
    }

    /// <summary>Decodes one inbound frame and, if it's a well-formed
    /// Presence frame, feeds it to <see cref="Receiver"/>. Any other frame
    /// type is ignored. Public so tests can drive it directly with
    /// hand-built <see cref="ControlFrame"/>s instead of needing a full live
    /// connection.</summary>
    public void HandleInboundFrame(ControlFrame frame)
    {
        if (frame.Type != ControlMessageType.Presence) return;

        PresenceLease lease;
        try
        {
            lease = PresenceFrameCodec.Decode(frame);
        }
        catch (MalformedFrameException)
        {
            // Same discipline as ChatService/AttentionCardService: a decode
            // failure this far past FrameDispatcher's checks means a
            // same-version peer bug, not an attack to react to by tearing
            // down the connection.
            return;
        }

        Receiver.Ingest(lease, _clock());
    }
}
