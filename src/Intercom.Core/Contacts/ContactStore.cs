using System.Text.Json;

namespace Intercom.Contacts;

/// <summary>
/// Persists the local, unilateral <see cref="Contact"/> groupings (issue #30;
/// docs/adr/0004-multi-device-contact-routing.md "Device-to-contact
/// association"). Not DPAPI-protected, for the same reasoning
/// <c>Intercom.Presence.DndSettingsStore</c>/<c>Intercom.Chat.ChatTtsSettingsStore</c>
/// give for their own plain-JSON choice: a contact name plus a list of
/// already-approved peer IDs carries no cryptographic material, grants no
/// trust, and reveals nothing about a peer beyond what the (already-
/// plaintext) approved-peer registry itself already reveals — this is
/// routing/presentation metadata, not identity/trust material, so plain JSON
/// under the app data directory is proportionate. For the same reason this
/// store also uses DndSettingsStore's simpler "missing or unreadable, for any
/// reason, defaults to empty" fail-safe rule rather than
/// <c>IdentityStore</c>'s corruption-vs-transient-failure distinction: the
/// cost of misjudging a read failure here is the contact list silently
/// starting empty for one launch, not fleet-wide re-pairing.
///
/// Deliberately never mutates <c>Intercom.Identity.IdentityStore</c> or its
/// <c>ApprovedPeer</c> records — grouping/ungrouping a peer here has zero
/// effect on that peer's trust/approval status, and vice versa.
/// </summary>
public sealed class ContactStore
{
    readonly string _path;
    readonly object _gate = new();
    readonly List<Contact> _contacts = [];

    /// <summary>Raised whenever <see cref="Create"/>, <see cref="Rename"/>,
    /// <see cref="Delete"/>, <see cref="AddMember"/>, or
    /// <see cref="RemoveMember"/> actually changes persisted state (never for
    /// a no-op). Raised with the internal lock released, mirroring this
    /// codebase's established discipline.</summary>
    public event Action? Changed;

    public ContactStore(string? appDataDirectory = null)
    {
        var dir = appDataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Intercom");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "contacts.json");
    }

    /// <summary>Loads the persisted contacts, or leaves the list empty if the
    /// file is missing or unreadable. Call once at startup.</summary>
    public void Load()
    {
        lock (_gate)
        {
            _contacts.Clear();
            _contacts.AddRange(TryRead());
        }
    }

    public IReadOnlyList<Contact> Contacts
    {
        get { lock (_gate) { return _contacts.ToArray(); } }
    }

    public Contact? Find(Guid contactId)
    {
        lock (_gate) { return _contacts.FirstOrDefault(c => c.ContactId == contactId); }
    }

    public Contact Create(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var contact = new Contact { ContactId = Guid.NewGuid(), Name = name, MemberPeerIds = [] };
        lock (_gate)
        {
            _contacts.Add(contact);
            Persist();
        }
        Changed?.Invoke();
        return contact;
    }

    /// <summary>False, with no write, if the contact is unknown.</summary>
    public bool Rename(Guid contactId, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!Mutate(contactId, existing => existing with { Name = name })) return false;
        Changed?.Invoke();
        return true;
    }

    /// <summary>False, with no write, if the contact is unknown. Only removes
    /// the local grouping — every member peer's approval status is
    /// untouched.</summary>
    public bool Delete(Guid contactId)
    {
        bool removed;
        lock (_gate)
        {
            removed = _contacts.RemoveAll(c => c.ContactId == contactId) > 0;
            if (removed) Persist();
        }
        if (removed) Changed?.Invoke();
        return removed;
    }

    /// <summary>Adds an already-approved peer to a contact's grouping.
    /// Idempotent: false, with no write, if the contact is unknown or the
    /// peer is already a member.</summary>
    public bool AddMember(Guid contactId, Guid peerId)
    {
        var changed = Mutate(contactId, existing => existing.MemberPeerIds.Contains(peerId)
            ? null
            : existing with { MemberPeerIds = [.. existing.MemberPeerIds, peerId] });
        if (changed) Changed?.Invoke();
        return changed;
    }

    /// <summary>Removes a peer from a contact's grouping. Idempotent: false,
    /// with no write, if the contact is unknown or the peer isn't currently a
    /// member. Never touches the peer's approval status.</summary>
    public bool RemoveMember(Guid contactId, Guid peerId)
    {
        var changed = Mutate(contactId, existing => existing.MemberPeerIds.Contains(peerId)
            ? existing with { MemberPeerIds = [.. existing.MemberPeerIds.Where(id => id != peerId)] }
            : null);
        if (changed) Changed?.Invoke();
        return changed;
    }

    /// <summary>Applies <paramref name="transform"/> to the contact if found;
    /// a null return from <paramref name="transform"/> means "no-op" (e.g.
    /// already in the desired state). Returns whether anything actually
    /// changed and was persisted.</summary>
    bool Mutate(Guid contactId, Func<Contact, Contact?> transform)
    {
        lock (_gate)
        {
            var index = _contacts.FindIndex(c => c.ContactId == contactId);
            if (index < 0) return false;

            var next = transform(_contacts[index]);
            if (next is null) return false;

            _contacts[index] = next;
            Persist();
            return true;
        }
    }

    List<Contact> TryRead()
    {
        if (!File.Exists(_path)) return [];
        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<List<Contact>>(json) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Contact store unreadable ({ex.GetType().Name}); starting empty.");
            return [];
        }
    }

    void Persist()
    {
        var json = JsonSerializer.Serialize(_contacts);
        var tempPath = _path + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _path, overwrite: true);
    }
}
