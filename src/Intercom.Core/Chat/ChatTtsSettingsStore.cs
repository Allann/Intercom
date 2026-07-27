using System.Text.Json;

namespace Intercom.Chat;

/// <summary>
/// Persists which approved peers have spoken chat (local text-to-speech on
/// receipt) turned on — issue #24's "optional per receiving peer" TTS
/// requirement. Keyed by peer ID, defaulting to off for any peer never
/// explicitly toggled on.
///
/// Not DPAPI-protected, for the same reasoning <c>Intercom.Presence.DndSettingsStore</c>
/// gives for its own plain-JSON choice: a set of peer IDs with a boolean
/// preference carries no cryptographic material, grants no trust, and
/// reveals nothing about a peer beyond what the (already-plaintext)
/// approved-peer registry itself already reveals — presentation/behavior
/// state, not identity/trust material, so plain JSON under the app data
/// directory is proportionate.
///
/// Also mirrors DndSettingsStore's simpler fail-safe rule rather than
/// IdentityStore's corruption-vs-transient-failure distinction: the cost of
/// misjudging a read failure here is TTS silently defaulting to off for one
/// launch, not fleet-wide re-pairing.
/// </summary>
public sealed class ChatTtsSettingsStore
{
    readonly string _path;
    readonly object _gate = new();
    readonly HashSet<Guid> _enabledPeerIds = [];
    string? _selectedVoiceId;

    public ChatTtsSettingsStore(string? appDataDirectory = null)
    {
        var dir = appDataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Intercom");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "chat-tts.json");
    }

    /// <summary>Raised whenever <see cref="SetEnabled"/> actually changes a
    /// peer's flag (never for a no-op). Raised with the internal lock
    /// released.</summary>
    public event Action<Guid, bool>? Changed;

    /// <summary>The locally-stored preferred <c>VoiceInformation.Id</c>
    /// (docs/research/windows-resident-app.md: "store the selected voice
    /// locally") — device-local, not per-peer, since it names one of this
    /// device's own installed voices. Null means "use the system default
    /// voice."</summary>
    public string? SelectedVoiceId
    {
        get { lock (_gate) { return _selectedVoiceId; } }
    }

    /// <summary>Loads the persisted set, or leaves everything off (and the
    /// voice unset) if the file is missing or unreadable. Call once at
    /// startup.</summary>
    public void Load()
    {
        lock (_gate)
        {
            _enabledPeerIds.Clear();
            var dto = TryRead();
            foreach (var id in dto.EnabledPeerIds) _enabledPeerIds.Add(id);
            _selectedVoiceId = dto.SelectedVoiceId;
        }
    }

    public bool IsEnabled(Guid peerId)
    {
        lock (_gate) { return _enabledPeerIds.Contains(peerId); }
    }

    public void SetEnabled(Guid peerId, bool enabled)
    {
        bool changed;
        lock (_gate)
        {
            changed = enabled ? _enabledPeerIds.Add(peerId) : _enabledPeerIds.Remove(peerId);
            if (changed) Persist();
        }
        if (changed) Changed?.Invoke(peerId, enabled);
    }

    public void SetSelectedVoiceId(string? voiceId)
    {
        lock (_gate)
        {
            if (_selectedVoiceId == voiceId) return;
            _selectedVoiceId = voiceId;
            Persist();
        }
    }

    ChatTtsDto TryRead()
    {
        if (!File.Exists(_path)) return new ChatTtsDto();
        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<ChatTtsDto>(json) ?? new ChatTtsDto();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Chat TTS settings unreadable ({ex.GetType().Name}); defaulting to all off.");
            return new ChatTtsDto();
        }
    }

    void Persist()
    {
        var json = JsonSerializer.Serialize(new ChatTtsDto { EnabledPeerIds = [.. _enabledPeerIds], SelectedVoiceId = _selectedVoiceId });
        var tempPath = _path + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _path, overwrite: true);
    }

    sealed class ChatTtsDto
    {
        public List<Guid> EnabledPeerIds { get; set; } = [];
        public string? SelectedVoiceId { get; set; }
    }
}
