namespace Intercom.Discovery;

/// <summary>
/// The non-secret peer-ID hint carried in a DNS-SD TXT record (ADR-0002):
/// LocalIdentity.PeerId rendered as a compact string, never the SPKI hash or
/// any certificate material. It has no security meaning — it only lets two
/// discovery records be recognized as "probably the same peer" (e.g. seen on
/// two interfaces) before any trust decision is made.
///
/// Wrapping the raw string (rather than passing a bare string around) gives
/// this a validated, fixed shape at construction time and equality that
/// matches the domain concept instead of incidental string equality — the
/// same reasoning as SpkiPin in Intercom.Identity.
/// </summary>
public readonly struct PeerIdHint : IEquatable<PeerIdHint>
{
    // The DNS TXT value is limited to 255 bytes total per key; a GUID "N"
    // format (32 hex chars) leaves comfortable headroom for the "id=" prefix
    // and any future TXT keys sharing the same record.
    const int MaxLength = 64;

    readonly string? _value;

    public PeerIdHint(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A peer-ID hint must not be empty.", nameof(value));
        if (value.Length > MaxLength)
            throw new ArgumentException($"A peer-ID hint must be at most {MaxLength} characters, got {value.Length}.", nameof(value));

        _value = value;
    }

    /// <summary>Derives the hint this device advertises from its own PeerId
    /// (ADR-0002: PeerId is already the non-secret routing identifier —
    /// nothing further needs deriving/truncating from it).</summary>
    public static PeerIdHint FromPeerId(Guid peerId) => new(peerId.ToString("N"));

    public string Value => RequireInitialized();

    public bool Equals(PeerIdHint other) =>
        string.Equals(RequireInitialized(), other.RequireInitialized(), StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is PeerIdHint other && Equals(other);
    public override int GetHashCode() => RequireInitialized().GetHashCode(StringComparison.Ordinal);
    public override string ToString() => RequireInitialized();

    public static bool operator ==(PeerIdHint left, PeerIdHint right) => left.Equals(right);
    public static bool operator !=(PeerIdHint left, PeerIdHint right) => !left.Equals(right);

    string RequireInitialized() => _value
        ?? throw new InvalidOperationException(
            "PeerIdHint was never assigned a value (e.g. default(PeerIdHint)) — it must be constructed with a non-empty string.");
}
