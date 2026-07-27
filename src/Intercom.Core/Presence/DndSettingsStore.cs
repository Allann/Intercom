using System.Text.Json;

namespace Intercom.Presence;

/// <summary>
/// Persists the explicit, locally-stored DND toggle
/// (docs/research/active-device-presence.md "DND": "an explicit, locally
/// stored Intercom setting... must not be inferred from Windows inactivity,
/// calendar state, or Windows Focus Sessions").
///
/// Unlike <c>Intercom.Identity.IdentityStore</c>, this is NOT DPAPI-
/// protected. IdentityStore's DPAPI encryption exists specifically for
/// identity/trust material (a private key, an approved-peer SPKI registry) —
/// data that is meaningfully sensitive and where "another local account or a
/// backup/sync tool can read this file" is a real concern. DND is neither: a
/// boolean interruption preference has no cryptographic material, grants no
/// trust, and identifies nothing about a peer. It is presentation/behavior
/// state, the same category as any other local app setting, so plain JSON
/// under the app data directory is proportionate — DPAPI would add
/// undecryptable-on-another-machine/profile friction for no corresponding
/// security benefit.
///
/// For the same reason, this store also does not replicate IdentityStore's
/// careful "corruption vs. transient I/O failure" distinction (see
/// IdentityStore's remarks) — the cost of misjudging a transient read
/// failure here is a UI toggle silently defaulting to off for one launch,
/// not fleet-wide re-pairing, so a single simpler fail-safe rule (missing or
/// unreadable, for any reason → off) is a deliberate, proportionate
/// simplification rather than an oversight.
/// </summary>
public sealed class DndSettingsStore
{
    readonly string _path;
    readonly object _gate = new();
    bool _dndEnabled;

    public bool DndEnabled { get { lock (_gate) { return _dndEnabled; } } }

    /// <summary>Raised whenever <see cref="Set"/> or <see cref="Toggle"/>
    /// actually changes the value (never for a no-op). Raised with the
    /// internal lock released, mirroring this repo's other stateful
    /// classes.</summary>
    public event Action<bool>? Changed;

    public DndSettingsStore(string? appDataDirectory = null)
    {
        var dir = appDataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Intercom");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "dnd.json");
    }

    /// <summary>Loads the persisted value, or leaves DND off if the file is
    /// missing or unreadable. Call once at startup, before reading
    /// <see cref="DndEnabled"/>.</summary>
    public void Load()
    {
        lock (_gate)
        {
            _dndEnabled = TryRead();
        }
    }

    /// <summary>Flips the value, persists it, and returns the new
    /// value.</summary>
    public bool Toggle()
    {
        bool newValue;
        lock (_gate)
        {
            newValue = !_dndEnabled;
            _dndEnabled = newValue;
            Persist(newValue);
        }
        Changed?.Invoke(newValue);
        return newValue;
    }

    public void Set(bool enabled)
    {
        bool changed;
        lock (_gate)
        {
            changed = _dndEnabled != enabled;
            _dndEnabled = enabled;
            if (changed) Persist(enabled);
        }
        if (changed) Changed?.Invoke(enabled);
    }

    bool TryRead()
    {
        if (!File.Exists(_path)) return false;
        try
        {
            var json = File.ReadAllText(_path);
            var dto = JsonSerializer.Deserialize<DndDto>(json);
            return dto?.DndEnabled ?? false;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"DND settings unreadable ({ex.GetType().Name}); defaulting to off.");
            return false;
        }
    }

    void Persist(bool enabled)
    {
        var json = JsonSerializer.Serialize(new DndDto { DndEnabled = enabled });
        var tempPath = _path + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _path, overwrite: true);
    }

    sealed class DndDto
    {
        public bool DndEnabled { get; set; }
    }
}
