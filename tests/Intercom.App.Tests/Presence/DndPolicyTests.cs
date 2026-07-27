using Intercom.Presence;
using Xunit;

namespace Intercom.App.Tests.Presence;

/// <summary>
/// docs/research/active-device-presence.md: "voice, hands-free requests,
/// attention chimes/cards, and interrupt requests stay silent/queued; text
/// still arrives silently." Also covers the acceptance criterion "DND always
/// beats recent activity for voice/chime routing" — this predicate takes no
/// activity input at all, so there's nothing for "recent activity" to even
/// override it with.
/// </summary>
public class DndPolicyTests
{
    [Theory]
    [InlineData(InteractionKind.Voice)]
    [InlineData(InteractionKind.HandsFreeRequest)]
    [InlineData(InteractionKind.AttentionChime)]
    [InlineData(InteractionKind.InterruptRequest)]
    public void DndOn_SuppressesEveryInterruptingKind(InteractionKind kind)
    {
        Assert.True(DndPolicy.IsSuppressed(dndEnabled: true, kind));
    }

    [Fact]
    public void DndOn_NeverSuppressesText()
    {
        Assert.False(DndPolicy.IsSuppressed(dndEnabled: true, InteractionKind.Text));
    }

    [Theory]
    [InlineData(InteractionKind.Voice)]
    [InlineData(InteractionKind.HandsFreeRequest)]
    [InlineData(InteractionKind.AttentionChime)]
    [InlineData(InteractionKind.InterruptRequest)]
    [InlineData(InteractionKind.Text)]
    public void DndOff_NeverSuppressesAnyKind(InteractionKind kind)
    {
        Assert.False(DndPolicy.IsSuppressed(dndEnabled: false, kind));
    }
}
