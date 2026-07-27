using Intercom.Routing;
using Xunit;

namespace Intercom.App.Tests.Routing;

/// <summary>
/// Persistence round-trip for <see cref="ManualOverrideStore"/>, mirroring
/// <c>DndSettingsStoreTests</c>'s pattern.
/// </summary>
public class ManualOverrideStoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "IntercomOverrideTests_" + Guid.NewGuid());

    public ManualOverrideStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Get_NoOverrideSet_ReturnsNull()
    {
        var store = new ManualOverrideStore(_dir);
        store.Load();

        Assert.Null(store.Get(Guid.NewGuid()));
    }

    [Fact]
    public void Set_ThenGet_ReturnsDevice()
    {
        var store = new ManualOverrideStore(_dir);
        store.Load();
        var contactId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();

        store.Set(contactId, deviceId);

        Assert.Equal(deviceId, store.Get(contactId));
    }

    [Fact]
    public void Set_PersistsAcrossFreshLoad()
    {
        var store = new ManualOverrideStore(_dir);
        store.Load();
        var contactId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        store.Set(contactId, deviceId);

        var reloaded = new ManualOverrideStore(_dir);
        reloaded.Load();

        Assert.Equal(deviceId, reloaded.Get(contactId));
    }

    [Fact]
    public void Set_Overwrites_PreviousOverrideForSameContact()
    {
        var store = new ManualOverrideStore(_dir);
        store.Load();
        var contactId = Guid.NewGuid();
        store.Set(contactId, Guid.NewGuid());
        var secondDevice = Guid.NewGuid();

        store.Set(contactId, secondDevice);

        Assert.Equal(secondDevice, store.Get(contactId));
    }

    [Fact]
    public void Clear_RemovesOverride_AndPersists()
    {
        var store = new ManualOverrideStore(_dir);
        store.Load();
        var contactId = Guid.NewGuid();
        store.Set(contactId, Guid.NewGuid());

        var cleared = store.Clear(contactId);

        Assert.True(cleared);
        Assert.Null(store.Get(contactId));

        var reloaded = new ManualOverrideStore(_dir);
        reloaded.Load();
        Assert.Null(reloaded.Get(contactId));
    }

    [Fact]
    public void Clear_NothingSet_ReturnsFalse()
    {
        var store = new ManualOverrideStore(_dir);
        store.Load();

        Assert.False(store.Clear(Guid.NewGuid()));
    }

    [Fact]
    public void Changed_RaisedWithDeviceId_OnSet_AndNull_OnClear()
    {
        var store = new ManualOverrideStore(_dir);
        store.Load();
        var contactId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var events = new List<(Guid ContactId, Guid? DeviceId)>();
        store.Changed += (c, d) => events.Add((c, d));

        store.Set(contactId, deviceId);
        store.Clear(contactId);

        Assert.Equal(2, events.Count);
        Assert.Equal((contactId, (Guid?)deviceId), events[0]);
        Assert.Equal((contactId, (Guid?)null), events[1]);
    }
}
