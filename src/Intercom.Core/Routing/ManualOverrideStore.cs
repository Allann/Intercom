using System.Text.Json;

namespace Intercom.Routing;

/// <summary>
/// Persists the per-contact "prefer this device for me right now" manual
/// override (issue #30; docs/adr/0004-multi-device-contact-routing.md
/// "Manual device override") — a HARD override, not a ranking input (see
/// <see cref="DeviceRoutingPolicy"/> for how it is actually applied). Local,
/// plain JSON, the same reasoning as
/// <c>Intercom.Presence.DndSettingsStore</c>/<c>Intercom.Chat.ChatTtsSettingsStore</c>/
/// <c>Intercom.Contacts.ContactStore</c>: this is a routing preference, not
/// trust/identity material, so DPAPI would add friction for no security
/// benefit.
///
/// Storage only knows "contact X currently prefers device Y" — it does not
/// itself know whether Y is still available. ADR-0004's "lapses
/// automatically if the device becomes unavailable" is applied by
/// <see cref="DeviceRoutingPolicy.ResolveTarget"/> each time it resolves a
/// target (via its <c>OverrideLapsed</c> result) — a caller wires that back
/// to <see cref="Clear"/>, mirroring the pure-policy/impure-store split every
/// other pair in this codebase uses.
/// </summary>
public sealed class ManualOverrideStore
{
    readonly string _path;
    readonly object _gate = new();
    readonly Dictionary<Guid, Guid> _overrides = []; // contactId -> preferred deviceId

    /// <summary>Raised whenever <see cref="Set"/> or <see cref="Clear"/>
    /// actually changes persisted state. <paramref name="deviceId"/> is null
    /// when the override was cleared.</summary>
    public event Action<Guid, Guid?>? Changed;

    public ManualOverrideStore(string? appDataDirectory = null)
    {
        var dir = appDataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Intercom");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "device-overrides.json");
    }

    /// <summary>Loads the persisted overrides, or leaves the set empty if the
    /// file is missing or unreadable. Call once at startup.</summary>
    public void Load()
    {
        lock (_gate)
        {
            _overrides.Clear();
            foreach (var kvp in TryRead()) _overrides[kvp.ContactId] = kvp.DeviceId;
        }
    }

    public Guid? Get(Guid contactId)
    {
        lock (_gate) { return _overrides.TryGetValue(contactId, out var deviceId) ? deviceId : null; }
    }

    public void Set(Guid contactId, Guid deviceId)
    {
        lock (_gate)
        {
            _overrides[contactId] = deviceId;
            Persist();
        }
        Changed?.Invoke(contactId, deviceId);
    }

    /// <summary>Clears the override for a contact, if any. Returns true if
    /// something was actually cleared. Used both for an explicit user
    /// "turn off" action and for <see cref="DeviceRoutingPolicy"/>'s
    /// auto-lapse.</summary>
    public bool Clear(Guid contactId)
    {
        bool removed;
        lock (_gate)
        {
            removed = _overrides.Remove(contactId);
            if (removed) Persist();
        }
        if (removed) Changed?.Invoke(contactId, null);
        return removed;
    }

    List<OverrideDto> TryRead()
    {
        if (!File.Exists(_path)) return [];
        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<List<OverrideDto>>(json) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Device override settings unreadable ({ex.GetType().Name}); defaulting to none.");
            return [];
        }
    }

    void Persist()
    {
        var dto = _overrides.Select(kvp => new OverrideDto { ContactId = kvp.Key, DeviceId = kvp.Value }).ToList();
        var json = JsonSerializer.Serialize(dto);
        var tempPath = _path + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _path, overwrite: true);
    }

    sealed class OverrideDto
    {
        public Guid ContactId { get; set; }
        public Guid DeviceId { get; set; }
    }
}
