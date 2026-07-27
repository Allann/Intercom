using Intercom.Chat;
using Xunit;

namespace Intercom.App.Tests.Chat;

/// <summary>
/// Persistence round-trip and fail-safe-to-off behavior for
/// <see cref="ChatTtsSettingsStore"/>, mirroring <c>DndSettingsStoreTests</c>'
/// fresh-temp-directory pattern.
/// </summary>
public class ChatTtsSettingsStoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "IntercomChatTtsTests_" + Guid.NewGuid());

    public ChatTtsSettingsStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Load_NoFileYet_EveryPeerDefaultsToOff()
    {
        var store = new ChatTtsSettingsStore(_dir);
        store.Load();

        Assert.False(store.IsEnabled(Guid.NewGuid()));
    }

    [Fact]
    public void SetEnabled_PersistsAcrossFreshLoad()
    {
        var peerId = Guid.NewGuid();
        var store = new ChatTtsSettingsStore(_dir);
        store.Load();

        store.SetEnabled(peerId, true);

        var reloaded = new ChatTtsSettingsStore(_dir);
        reloaded.Load();
        Assert.True(reloaded.IsEnabled(peerId));
    }

    [Fact]
    public void SetEnabled_IsPerPeer_DoesNotAffectOtherPeers()
    {
        var peerA = Guid.NewGuid();
        var peerB = Guid.NewGuid();
        var store = new ChatTtsSettingsStore(_dir);
        store.Load();

        store.SetEnabled(peerA, true);

        Assert.True(store.IsEnabled(peerA));
        Assert.False(store.IsEnabled(peerB));
    }

    [Fact]
    public void SetEnabled_False_RemovesPreviouslyEnabledPeer()
    {
        var peerId = Guid.NewGuid();
        var store = new ChatTtsSettingsStore(_dir);
        store.Load();
        store.SetEnabled(peerId, true);

        store.SetEnabled(peerId, false);

        Assert.False(store.IsEnabled(peerId));
        var reloaded = new ChatTtsSettingsStore(_dir);
        reloaded.Load();
        Assert.False(reloaded.IsEnabled(peerId));
    }

    [Fact]
    public void SetEnabled_SameValue_DoesNotRaiseChanged()
    {
        var store = new ChatTtsSettingsStore(_dir);
        store.Load();
        var raised = 0;
        store.Changed += (_, _) => raised++;

        store.SetEnabled(Guid.NewGuid(), false); // already off — no-op

        Assert.Equal(0, raised);
    }

    [Fact]
    public void SetEnabled_DifferentValue_RaisesChangedWithPeerIdAndNewValue()
    {
        var peerId = Guid.NewGuid();
        var store = new ChatTtsSettingsStore(_dir);
        store.Load();
        (Guid PeerId, bool Enabled)? observed = null;
        store.Changed += (id, enabled) => observed = (id, enabled);

        store.SetEnabled(peerId, true);

        Assert.Equal(peerId, observed!.Value.PeerId);
        Assert.True(observed.Value.Enabled);
    }

    [Fact]
    public void SelectedVoiceId_DefaultsToNull()
    {
        var store = new ChatTtsSettingsStore(_dir);
        store.Load();

        Assert.Null(store.SelectedVoiceId);
    }

    [Fact]
    public void SetSelectedVoiceId_PersistsAcrossFreshLoad()
    {
        var store = new ChatTtsSettingsStore(_dir);
        store.Load();

        store.SetSelectedVoiceId("Microsoft David Desktop - English (United States)");

        var reloaded = new ChatTtsSettingsStore(_dir);
        reloaded.Load();
        Assert.Equal("Microsoft David Desktop - English (United States)", reloaded.SelectedVoiceId);
    }

    [Fact]
    public void SetSelectedVoiceId_AndSetEnabled_BothPersistTogether()
    {
        var peerId = Guid.NewGuid();
        var store = new ChatTtsSettingsStore(_dir);
        store.Load();

        store.SetEnabled(peerId, true);
        store.SetSelectedVoiceId("some-voice-id");

        var reloaded = new ChatTtsSettingsStore(_dir);
        reloaded.Load();
        Assert.True(reloaded.IsEnabled(peerId));
        Assert.Equal("some-voice-id", reloaded.SelectedVoiceId);
    }

    [Fact]
    public void Load_CorruptFile_DefaultsToOff_DoesNotThrow()
    {
        var path = Path.Combine(_dir, "chat-tts.json");
        File.WriteAllText(path, "{ not valid json ");

        var store = new ChatTtsSettingsStore(_dir);
        store.Load();

        Assert.False(store.IsEnabled(Guid.NewGuid()));
    }
}
