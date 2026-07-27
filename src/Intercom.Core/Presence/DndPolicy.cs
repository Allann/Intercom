namespace Intercom.Presence;

/// <summary>
/// The DND suppression rule, exposed as a pure predicate for other tickets'
/// not-yet-built features to call into once they exist (#24 text chat, #25
/// attention cards, #29 group voice). Issue #23 builds only the DND state
/// (<see cref="DndSettingsStore"/>) and this rule, deliberately not the
/// wiring into those features.
///
/// docs/research/active-device-presence.md: "voice, hands-free requests,
/// attention chimes/cards, and interrupt requests stay silent/queued; text
/// still arrives silently" — so <see cref="InteractionKind.Text"/> is never
/// suppressed by DND (there is nothing to interrupt), while every other kind
/// is suppressed whenever DND is on.
///
/// This predicate deliberately takes no availability/idle input at all: "DND
/// always beats recent activity for voice/chime routing" means it must never
/// be satisfied by "the user looks active right now," only ever by the
/// explicit, locally stored DND bit.
/// </summary>
public static class DndPolicy
{
    public static bool IsSuppressed(bool dndEnabled, InteractionKind kind) =>
        dndEnabled && kind != InteractionKind.Text;
}
