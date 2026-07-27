using Intercom.Updates;
using Xunit;

namespace Intercom.App.Tests.Updates;

/// <summary>
/// Issue #31's orchestration piece: never blocks/throws when the version
/// source is unreachable or misbehaving, surfaces a notice only for a
/// genuinely newer, not-yet-dismissed release, and remembers a dismissal
/// per-version (reappearing for a newer release, not for the same one).
/// Hand-written fakes only, matching every other test file in this project
/// — no mocking framework.
/// </summary>
public class UpdateCheckerTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "IntercomTests_" + Guid.NewGuid());

    public UpdateCheckerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    UpdateChecker MakeChecker(IUpdateVersionSource source, AppVersion? runningVersion) =>
        new(source, new FakeRunningVersionProvider(runningVersion), new UpdateNoticeStore(_dir));

    [Fact]
    public async Task CheckAsync_NewerReleaseAvailable_ReturnsNotice()
    {
        var source = new FakeUpdateVersionSource(new LatestRelease("v2.0.0", "https://example.test/releases/v2.0.0"));
        var checker = MakeChecker(source, new AppVersion(1, 0, 0, 0));

        var notice = await checker.CheckAsync();

        Assert.NotNull(notice);
        Assert.Equal(new AppVersion(2, 0, 0, 0), notice!.Version);
        Assert.Equal("https://example.test/releases/v2.0.0", notice.ReleaseUrl);
    }

    [Fact]
    public async Task CheckAsync_SameVersionAsRunning_ReturnsNull()
    {
        var source = new FakeUpdateVersionSource(new LatestRelease("v1.0.0", "https://example.test/releases/v1.0.0"));
        var checker = MakeChecker(source, new AppVersion(1, 0, 0, 0));

        var notice = await checker.CheckAsync();

        Assert.Null(notice);
    }

    [Fact]
    public async Task CheckAsync_RunningVersionIsNewerThanRemote_ReturnsNull()
    {
        var source = new FakeUpdateVersionSource(new LatestRelease("v0.9.0", "https://example.test/releases/v0.9.0"));
        var checker = MakeChecker(source, new AppVersion(1, 0, 0, 0));

        var notice = await checker.CheckAsync();

        Assert.Null(notice);
    }

    [Fact]
    public async Task CheckAsync_VersionSourceReturnsNull_ReturnsNull()
    {
        // Models an unreachable network, a 404 (no releases published yet —
        // true of the real repo today), or a rate-limited response: the real
        // GitHubReleaseVersionSource turns all of these into null, never an
        // exception.
        var source = new FakeUpdateVersionSource(latestRelease: null);
        var checker = MakeChecker(source, new AppVersion(1, 0, 0, 0));

        var notice = await checker.CheckAsync();

        Assert.Null(notice);
    }

    [Fact]
    public async Task CheckAsync_VersionSourceThrows_DoesNotThrowAndReturnsNull()
    {
        // IUpdateVersionSource's own contract says "never throws", but
        // UpdateChecker.CheckAsync wraps the call anyway as defense in
        // depth — this proves that defense actually works, not just the
        // interface's documented contract.
        var source = new ThrowingUpdateVersionSource();
        var checker = MakeChecker(source, new AppVersion(1, 0, 0, 0));

        var notice = await checker.CheckAsync();

        Assert.Null(notice);
    }

    [Fact]
    public async Task CheckAsync_MalformedRemoteTag_ReturnsNullRatherThanThrowing()
    {
        var source = new FakeUpdateVersionSource(new LatestRelease("not-a-version", "https://example.test"));
        var checker = MakeChecker(source, new AppVersion(1, 0, 0, 0));

        var notice = await checker.CheckAsync();

        Assert.Null(notice);
    }

    [Fact]
    public async Task CheckAsync_RunningVersionUnavailable_ReturnsNull()
    {
        // Models the unpackaged dev inner-loop case (Package.Current throws)
        // — PackageRunningVersionProvider turns that into null, never an
        // exception, and the check simply has no basis for comparison.
        var source = new FakeUpdateVersionSource(new LatestRelease("v9.9.9", "https://example.test"));
        var checker = MakeChecker(source, runningVersion: null);

        var notice = await checker.CheckAsync();

        Assert.Null(notice);
    }

    [Fact]
    public async Task CheckAsync_NoticeAlreadyDismissedForThisVersion_DoesNotReappear()
    {
        var source = new FakeUpdateVersionSource(new LatestRelease("v2.0.0", "https://example.test/v2"));
        var checker = MakeChecker(source, new AppVersion(1, 0, 0, 0));

        checker.Dismiss(new AppVersion(2, 0, 0, 0));
        var notice = await checker.CheckAsync();

        Assert.Null(notice);
    }

    [Fact]
    public async Task CheckAsync_NewerReleaseThanTheDismissedOne_ReappearsWithTheNewNotice()
    {
        var checker = MakeChecker(
            new FakeUpdateVersionSource(new LatestRelease("v2.0.0", "https://example.test/v2")),
            new AppVersion(1, 0, 0, 0));
        checker.Dismiss(new AppVersion(2, 0, 0, 0));

        // A newer release ships after the dismissal — new UpdateChecker
        // instance (same store directory) models a fresh app launch reading
        // the same persisted dismissal.
        var laterChecker = MakeChecker(
            new FakeUpdateVersionSource(new LatestRelease("v2.1.0", "https://example.test/v2.1")),
            new AppVersion(1, 0, 0, 0));

        var notice = await laterChecker.CheckAsync();

        Assert.NotNull(notice);
        Assert.Equal(new AppVersion(2, 1, 0, 0), notice!.Version);
    }

    [Fact]
    public async Task Dismiss_PersistsAcrossNewCheckerInstances()
    {
        var firstChecker = MakeChecker(
            new FakeUpdateVersionSource(new LatestRelease("v2.0.0", "https://example.test/v2")),
            new AppVersion(1, 0, 0, 0));
        var firstNotice = await firstChecker.CheckAsync();
        Assert.NotNull(firstNotice);
        firstChecker.Dismiss(firstNotice!.Version);

        var secondChecker = MakeChecker(
            new FakeUpdateVersionSource(new LatestRelease("v2.0.0", "https://example.test/v2")),
            new AppVersion(1, 0, 0, 0));
        var secondNotice = await secondChecker.CheckAsync();

        Assert.Null(secondNotice);
    }

    sealed class FakeUpdateVersionSource(LatestRelease? latestRelease) : IUpdateVersionSource
    {
        public Task<LatestRelease?> GetLatestReleaseAsync(CancellationToken cancellationToken) =>
            Task.FromResult(latestRelease);
    }

    sealed class ThrowingUpdateVersionSource : IUpdateVersionSource
    {
        public Task<LatestRelease?> GetLatestReleaseAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated network failure");
    }

    sealed class FakeRunningVersionProvider(AppVersion? version) : IRunningAppVersionProvider
    {
        public AppVersion? GetRunningVersion() => version;
    }
}
