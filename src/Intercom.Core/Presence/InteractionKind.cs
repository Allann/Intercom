namespace Intercom.Presence;

/// <summary>The kinds of interaction DND enforcement
/// (docs/research/active-device-presence.md "DND") distinguishes. Voice,
/// HandsFreeRequest, AttentionChime, and InterruptRequest are interrupting;
/// Text is not — DND never blocks text delivery, only its audible/visual
/// interruption.</summary>
public enum InteractionKind
{
    Voice,
    HandsFreeRequest,
    AttentionChime,
    InterruptRequest,
    Text,
}
