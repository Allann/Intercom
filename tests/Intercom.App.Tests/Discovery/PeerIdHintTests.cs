using Intercom.Discovery;
using Xunit;

namespace Intercom.App.Tests.Discovery;

public class PeerIdHintTests
{
    [Fact]
    public void FromPeerId_IsDeterministicForTheSameGuid()
    {
        var guid = Guid.NewGuid();
        Assert.Equal(PeerIdHint.FromPeerId(guid), PeerIdHint.FromPeerId(guid));
    }

    [Fact]
    public void EqualValues_AreEqual()
    {
        Assert.Equal(new PeerIdHint("abc"), new PeerIdHint("abc"));
    }

    [Fact]
    public void DifferentValues_AreNotEqual()
    {
        Assert.NotEqual(new PeerIdHint("abc"), new PeerIdHint("xyz"));
    }

    [Fact]
    public void Empty_Throws()
    {
        Assert.Throws<ArgumentException>(() => new PeerIdHint(""));
    }

    [Fact]
    public void Whitespace_Throws()
    {
        Assert.Throws<ArgumentException>(() => new PeerIdHint("   "));
    }

    [Fact]
    public void TooLong_Throws()
    {
        Assert.Throws<ArgumentException>(() => new PeerIdHint(new string('a', 65)));
    }

    [Fact]
    public void Default_ThrowsOnUse()
    {
        var hint = default(PeerIdHint);
        Assert.Throws<InvalidOperationException>(() => hint.Value);
    }
}
