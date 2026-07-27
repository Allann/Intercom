using Intercom.Contacts;
using Xunit;

namespace Intercom.App.Tests.Contacts;

/// <summary>
/// Persistence round-trip and membership-mutation behavior for
/// <see cref="ContactStore"/>. Uses a fresh temp directory per test, the same
/// pattern <c>DndSettingsStoreTests</c> uses for <c>DndSettingsStore</c>.
/// </summary>
public class ContactStoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "IntercomContactTests_" + Guid.NewGuid());

    public ContactStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Load_NoFileYet_StartsEmpty()
    {
        var store = new ContactStore(_dir);
        store.Load();
        Assert.Empty(store.Contacts);
    }

    [Fact]
    public void Create_AddsContactWithNoMembers()
    {
        var store = new ContactStore(_dir);
        store.Load();

        var contact = store.Create("Mum");

        Assert.Equal("Mum", contact.Name);
        Assert.Empty(contact.MemberPeerIds);
        Assert.Equal(contact, Assert.Single(store.Contacts));
    }

    [Fact]
    public void Create_PersistsAcrossFreshLoad()
    {
        var store = new ContactStore(_dir);
        store.Load();
        var contact = store.Create("Dad");

        var reloaded = new ContactStore(_dir);
        reloaded.Load();

        var found = reloaded.Find(contact.ContactId);
        Assert.NotNull(found);
        Assert.Equal("Dad", found!.Name);
    }

    [Fact]
    public void AddMember_AddsApprovedPeerToGrouping_AndPersists()
    {
        var store = new ContactStore(_dir);
        store.Load();
        var contact = store.Create("Mum");
        var peerId = Guid.NewGuid();

        var changed = store.AddMember(contact.ContactId, peerId);

        Assert.True(changed);
        Assert.Contains(peerId, store.Find(contact.ContactId)!.MemberPeerIds);

        var reloaded = new ContactStore(_dir);
        reloaded.Load();
        Assert.Contains(peerId, reloaded.Find(contact.ContactId)!.MemberPeerIds);
    }

    [Fact]
    public void AddMember_AlreadyAMember_IsNoOp()
    {
        var store = new ContactStore(_dir);
        store.Load();
        var contact = store.Create("Mum");
        var peerId = Guid.NewGuid();
        store.AddMember(contact.ContactId, peerId);

        var changed = store.AddMember(contact.ContactId, peerId);

        Assert.False(changed);
        Assert.Single(store.Find(contact.ContactId)!.MemberPeerIds);
    }

    [Fact]
    public void AddMember_UnknownContact_ReturnsFalse()
    {
        var store = new ContactStore(_dir);
        store.Load();

        Assert.False(store.AddMember(Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public void RemoveMember_RemovesGroupingOnly()
    {
        var store = new ContactStore(_dir);
        store.Load();
        var contact = store.Create("Mum");
        var peerId = Guid.NewGuid();
        store.AddMember(contact.ContactId, peerId);

        var changed = store.RemoveMember(contact.ContactId, peerId);

        Assert.True(changed);
        Assert.DoesNotContain(peerId, store.Find(contact.ContactId)!.MemberPeerIds);
    }

    [Fact]
    public void RemoveMember_NotAMember_IsNoOp()
    {
        var store = new ContactStore(_dir);
        store.Load();
        var contact = store.Create("Mum");

        Assert.False(store.RemoveMember(contact.ContactId, Guid.NewGuid()));
    }

    [Fact]
    public void Rename_ChangesNameOnly_MembershipUntouched()
    {
        var store = new ContactStore(_dir);
        store.Load();
        var contact = store.Create("Mum");
        var peerId = Guid.NewGuid();
        store.AddMember(contact.ContactId, peerId);

        Assert.True(store.Rename(contact.ContactId, "Mother"));

        var updated = store.Find(contact.ContactId)!;
        Assert.Equal("Mother", updated.Name);
        Assert.Contains(peerId, updated.MemberPeerIds);
    }

    [Fact]
    public void Delete_RemovesContact_AndPersists()
    {
        var store = new ContactStore(_dir);
        store.Load();
        var contact = store.Create("Mum");

        Assert.True(store.Delete(contact.ContactId));
        Assert.Null(store.Find(contact.ContactId));

        var reloaded = new ContactStore(_dir);
        reloaded.Load();
        Assert.Null(reloaded.Find(contact.ContactId));
    }

    [Fact]
    public void Delete_UnknownContact_ReturnsFalse()
    {
        var store = new ContactStore(_dir);
        store.Load();

        Assert.False(store.Delete(Guid.NewGuid()));
    }

    [Fact]
    public void Changed_RaisedOnlyOnActualMutation()
    {
        var store = new ContactStore(_dir);
        store.Load();
        var raiseCount = 0;
        store.Changed += () => raiseCount++;

        var contact = store.Create("Mum"); // 1
        var peerId = Guid.NewGuid();
        store.AddMember(contact.ContactId, peerId); // 2
        store.AddMember(contact.ContactId, peerId); // no-op, not counted
        store.RemoveMember(contact.ContactId, Guid.NewGuid()); // no-op, not counted

        Assert.Equal(2, raiseCount);
    }
}
