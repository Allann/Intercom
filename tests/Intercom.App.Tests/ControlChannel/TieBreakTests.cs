using Intercom.ControlChannel;
using Intercom.Identity;
using Xunit;

namespace Intercom.App.Tests.ControlChannel;

public class TieBreakTests
{
    static SpkiPin Pin(byte seed) => new(Enumerable.Repeat(seed, 32).ToArray());

    [Fact]
    public void LowerLocalHash_InitiatesAsClient()
    {
        var result = TieBreak.Resolve(Pin(1), Pin(2));
        Assert.Equal(TieBreakRole.InitiateAsClient, result);
    }

    [Fact]
    public void HigherLocalHash_WaitsAndAccepts()
    {
        var result = TieBreak.Resolve(Pin(2), Pin(1));
        Assert.Equal(TieBreakRole.WaitAndAccept, result);
    }

    [Fact]
    public void BothSidesAgree_ExactlyOneInitiates()
    {
        // The whole point: both peers independently compute this from the
        // same two values with no coordination, and must never both decide
        // to initiate (nor both decide to wait).
        var a = Pin(10);
        var b = Pin(20);

        var fromA = TieBreak.Resolve(a, b);
        var fromB = TieBreak.Resolve(b, a);

        Assert.NotEqual(fromA, fromB);
    }

    [Fact]
    public void IdenticalHashes_Throws()
    {
        var pin = Pin(5);
        Assert.Throws<InvalidOperationException>(() => TieBreak.Resolve(pin, pin));
    }

    [Fact]
    public void SameRuleAppliesToReconnectAsToInitialConnect()
    {
        // ADR-0001: reconnects reuse the identical rule — asserted here as
        // "calling Resolve again with the same two hashes always gives the
        // same answer," which is what makes reuse safe/meaningful.
        var local = Pin(3);
        var remote = Pin(7);

        var first = TieBreak.Resolve(local, remote);
        var second = TieBreak.Resolve(local, remote);

        Assert.Equal(first, second);
    }
}
