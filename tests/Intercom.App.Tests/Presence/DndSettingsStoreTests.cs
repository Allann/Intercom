using Intercom.Presence;
using Xunit;

namespace Intercom.App.Tests.Presence;

/// <summary>
/// Persistence round-trip and fail-safe-to-off behavior for
/// <see cref="DndSettingsStore"/>. Uses a fresh temp directory per test, the
/// same pattern IdentityStoreTests uses for IdentityStore.
/// </summary>
public class DndSettingsStoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "IntercomDndTests_" + Guid.NewGuid());

    public DndSettingsStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Load_NoFileYet_DefaultsToOff()
    {
        var store = new DndSettingsStore(_dir);
        store.Load();
        Assert.False(store.DndEnabled);
    }

    [Fact]
    public void Toggle_PersistsAcrossFreshLoad()
    {
        var store = new DndSettingsStore(_dir);
        store.Load();

        var newValue = store.Toggle();
        Assert.True(newValue);

        var reloaded = new DndSettingsStore(_dir);
        reloaded.Load();
        Assert.True(reloaded.DndEnabled);
    }

    [Fact]
    public void Toggle_TwiceReturnsToOff_AndPersists()
    {
        var store = new DndSettingsStore(_dir);
        store.Load();
        store.Toggle();
        store.Toggle();

        var reloaded = new DndSettingsStore(_dir);
        reloaded.Load();
        Assert.False(reloaded.DndEnabled);
    }

    [Fact]
    public void Set_SameValue_DoesNotRaiseChanged()
    {
        var store = new DndSettingsStore(_dir);
        store.Load();
        var raised = 0;
        store.Changed += _ => raised++;

        store.Set(false); // already false — no-op

        Assert.Equal(0, raised);
    }

    [Fact]
    public void Set_DifferentValue_RaisesChangedWithNewValue()
    {
        var store = new DndSettingsStore(_dir);
        store.Load();
        bool? observed = null;
        store.Changed += v => observed = v;

        store.Set(true);

        Assert.True(observed);
    }

    [Fact]
    public void Load_CorruptFile_DefaultsToOff_DoesNotThrow()
    {
        var path = Path.Combine(_dir, "dnd.json");
        File.WriteAllText(path, "{ not valid json ");

        var store = new DndSettingsStore(_dir);
        store.Load();

        Assert.False(store.DndEnabled);
    }
}
