using Intercom.Updates;
using Xunit;

namespace Intercom.App.Tests.Updates;

/// <summary>
/// The correctness-critical, pure-policy piece of issue #31: parsing a
/// GitHub release tag into the same four-part shape as the packaged app's
/// own <c>Windows.ApplicationModel.PackageVersion</c>, and comparing the two.
/// A misparse here could either silently hide a real update (false "up to
/// date") or nag forever (a tag that never compares as "already seen"), so
/// every edge case issue #31 calls out is covered here.
/// </summary>
public class AppVersionTests
{
    [Theory]
    [InlineData("v1.2.3", 1, 2, 3, 0)]
    [InlineData("V1.2.3", 1, 2, 3, 0)]
    [InlineData("1.2.3", 1, 2, 3, 0)]
    [InlineData("1.2.3.4", 1, 2, 3, 4)]
    [InlineData("v1.2", 1, 2, 0, 0)]
    [InlineData("v1", 1, 0, 0, 0)]
    [InlineData("0.0.0.0", 0, 0, 0, 0)]
    public void TryParse_WellFormedTag_ParsesEachPart(string tag, int major, int minor, int build, int revision)
    {
        var ok = AppVersion.TryParse(tag, out var version);

        Assert.True(ok);
        Assert.Equal(new AppVersion(major, minor, build, revision), version);
    }

    [Theory]
    [InlineData("v2.0.0-beta.1", 2, 0, 0, 0)]
    [InlineData("1.2.3-rc1", 1, 2, 3, 0)]
    [InlineData("1.2.3+buildmeta", 1, 2, 3, 0)]
    [InlineData("v1.2.3.4-hotfix", 1, 2, 3, 4)]
    public void TryParse_PrereleaseOrBuildMetadataSuffix_IgnoresSuffix(string tag, int major, int minor, int build, int revision)
    {
        var ok = AppVersion.TryParse(tag, out var version);

        Assert.True(ok);
        Assert.Equal(new AppVersion(major, minor, build, revision), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("latest")]
    [InlineData("not-a-version")]
    [InlineData("v")]
    [InlineData("v.")]
    [InlineData("release-candidate")]
    public void TryParse_MalformedTag_ReturnsFalse(string? tag)
    {
        var ok = AppVersion.TryParse(tag, out var version);

        Assert.False(ok);
        Assert.Equal(AppVersion.Zero, version);
    }

    [Fact]
    public void TryParse_NumericPartTooLargeForInt_ReturnsFalseRatherThanThrowing()
    {
        var ok = AppVersion.TryParse("v99999999999999999999.0.0.0", out var version);

        Assert.False(ok);
        Assert.Equal(AppVersion.Zero, version);
    }

    [Fact]
    public void Comparison_EqualVersions_NeitherIsNewer()
    {
        var a = new AppVersion(1, 2, 3, 4);
        var b = new AppVersion(1, 2, 3, 4);

        Assert.False(a > b);
        Assert.False(a < b);
        Assert.True(a >= b);
        Assert.True(a <= b);
        Assert.Equal(0, a.CompareTo(b));
    }

    [Theory]
    [InlineData(2, 0, 0, 0, 1, 0, 0, 0)] // major
    [InlineData(1, 3, 0, 0, 1, 2, 0, 0)] // minor
    [InlineData(1, 2, 4, 0, 1, 2, 3, 0)] // build
    [InlineData(1, 2, 3, 5, 1, 2, 3, 4)] // revision
    public void Comparison_NewerRunningVersion_ComparesGreater(
        int newMajor, int newMinor, int newBuild, int newRevision,
        int oldMajor, int oldMinor, int oldBuild, int oldRevision)
    {
        var newer = new AppVersion(newMajor, newMinor, newBuild, newRevision);
        var older = new AppVersion(oldMajor, oldMinor, oldBuild, oldRevision);

        Assert.True(newer > older);
        Assert.True(older < newer);
    }

    [Fact]
    public void ToString_RoundTripsThroughTryParse()
    {
        var version = new AppVersion(3, 1, 4, 15);

        var ok = AppVersion.TryParse(version.ToString(), out var parsed);

        Assert.True(ok);
        Assert.Equal(version, parsed);
    }
}
