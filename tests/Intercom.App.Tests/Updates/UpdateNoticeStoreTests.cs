using Intercom.Updates;
using Xunit;

namespace Intercom.App.Tests.Updates;

/// <summary>
/// Direct coverage of the marker-file persistence <see cref="UpdateChecker"/>
/// builds on, mirroring <c>Intercom.Diagnostics.CrashMarker</c>'s "small
/// local marker file" model applied to a version instead of a boolean.
/// </summary>
public class UpdateNoticeStoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "IntercomTests_" + Guid.NewGuid());

    public UpdateNoticeStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void GetDismissedVersion_NoMarkerWritten_ReturnsNull()
    {
        var store = new UpdateNoticeStore(_dir);

        Assert.Null(store.GetDismissedVersion());
    }

    [Fact]
    public void MarkDismissed_ThenGetDismissedVersion_RoundTrips()
    {
        var store = new UpdateNoticeStore(_dir);

        store.MarkDismissed(new AppVersion(1, 2, 3, 4));

        Assert.Equal(new AppVersion(1, 2, 3, 4), store.GetDismissedVersion());
    }

    [Fact]
    public void MarkDismissed_Twice_LatestValueWins()
    {
        var store = new UpdateNoticeStore(_dir);

        store.MarkDismissed(new AppVersion(1, 0, 0, 0));
        store.MarkDismissed(new AppVersion(2, 0, 0, 0));

        Assert.Equal(new AppVersion(2, 0, 0, 0), store.GetDismissedVersion());
    }

    [Fact]
    public void GetDismissedVersion_CorruptMarkerFile_ReturnsNullRatherThanThrowing()
    {
        var store = new UpdateNoticeStore(_dir);
        File.WriteAllText(Path.Combine(_dir, "update-dismissed.marker"), "not a version at all");

        Assert.Null(store.GetDismissedVersion());
    }

    [Fact]
    public void TwoStoreInstances_OverTheSameDirectory_ShareState()
    {
        var first = new UpdateNoticeStore(_dir);
        first.MarkDismissed(new AppVersion(3, 1, 4, 0));

        var second = new UpdateNoticeStore(_dir);

        Assert.Equal(new AppVersion(3, 1, 4, 0), second.GetDismissedVersion());
    }
}
