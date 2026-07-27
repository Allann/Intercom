using Intercom.Identity;
using Xunit;

namespace Intercom.App.Tests;

public class SpkiPinTests
{
    static byte[] Hash(byte seed) => Enumerable.Repeat(seed, 32).ToArray();

    [Fact]
    public void Constructor_RejectsWrongLength()
    {
        Assert.Throws<ArgumentException>(() => new SpkiPin(new byte[31]));
        Assert.Throws<ArgumentException>(() => new SpkiPin(new byte[33]));
    }

    [Fact]
    public void Equality_IsStructural_NotReference()
    {
        var a = new SpkiPin(Hash(7));
        var b = new SpkiPin(Hash(7)); // separate array instance, same bytes
        Assert.Equal(a, b);
        Assert.True(a == b);
    }

    [Fact]
    public void Equality_DiffersOnDifferentBytes()
    {
        var a = new SpkiPin(Hash(1));
        var b = new SpkiPin(Hash(2));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Constructor_ClonesInput_MutatingCallerArrayDoesNotAffectPin()
    {
        var bytes = Hash(9);
        var pin = new SpkiPin(bytes);
        bytes[0] = (byte)(bytes[0] + 1);
        Assert.NotEqual(bytes.AsSpan().ToArray(), pin.Bytes.ToArray());
    }

    [Fact]
    public void Default_ThrowsRatherThanActingAsValidEmptyPin()
    {
        var uninitialized = default(SpkiPin);
        Assert.Throws<InvalidOperationException>(() => uninitialized.Bytes.ToArray());
        Assert.Throws<InvalidOperationException>(() => uninitialized.ToString());
    }
}
