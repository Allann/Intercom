namespace Intercom.ControlChannel;

/// <summary>
/// The static protocol capability set exchanged once via <see cref="Hello"/>
/// (docs/mvp-specification.md §2: "protocol version + static capability set:
/// text, send_audio, receive_audio, tts, spoken_chat, group_floor,
/// attention_cards"). Flags so the whole set travels as one bitmask in the
/// Hello payload.
///
/// Deliberately distinct from the presence lease's transient per-contact
/// capability field (ADR-0001): this describes what the PROTOCOL/build
/// supports, evaluated once per connection; presence capabilities describe
/// transient per-contact routing state that changes far more often and is
/// out of this ticket's scope (issue #23).
/// </summary>
[Flags]
public enum Capability
{
    None = 0,
    Text = 1 << 0,
    SendAudio = 1 << 1,
    ReceiveAudio = 1 << 2,
    Tts = 1 << 3,
    SpokenChat = 1 << 4,
    GroupFloor = 1 << 5,
    AttentionCards = 1 << 6,
    ChatMarkdown = 1 << 7,
    ChatImages = 1 << 8,
    ChatTyping = 1 << 9,
}
