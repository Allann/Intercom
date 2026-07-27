using Intercom.ControlChannel;
using Intercom.Identity;
using Xunit;

namespace Intercom.App.Tests.ControlChannel;

public class SpkiPinConstantTimeComparerTests
{
    static SpkiPin Pin(byte seed) => new(Enumerable.Repeat(seed, 32).ToArray());

    [Fact]
    public void EqualPins_MatchesTrue()
    {
        Assert.True(SpkiPinConstantTimeComparer.Matches(Pin(1), Pin(1)));
    }

    [Fact]
    public void DifferentPins_MatchesFalse()
    {
        Assert.False(SpkiPinConstantTimeComparer.Matches(Pin(1), Pin(2)));
    }

    [Fact]
    public void DiffersOnlyInLastByte_StillDetectedAsMismatch()
    {
        var a = new SpkiPin(Enumerable.Repeat((byte)5, 32).ToArray());
        var bBytes = Enumerable.Repeat((byte)5, 32).ToArray();
        bBytes[31] = 6;
        var b = new SpkiPin(bBytes);

        Assert.False(SpkiPinConstantTimeComparer.Matches(a, b));
    }
}
