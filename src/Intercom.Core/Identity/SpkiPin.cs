using System.Text.Json.Serialization;

namespace Intercom.Identity;

/// <summary>
/// A validated, immutable SHA-256 SPKI pin (ADR-0002). Wrapping this instead
/// of a bare byte[] does two things: it rejects a malformed pin at
/// construction rather than at first comparison, and it gives value
/// (structural) equality — a bare byte[] on an ApprovedPeer record would
/// otherwise compare by reference, silently breaking peer-lookup equality
/// for two logically-identical pins loaded from separate deserializations.
///
/// Two invariants a struct can't fully close at the language level are
/// handled deliberately rather than silently:
/// - The constructor defensively clones its input so the caller's array
///   can't mutate this pin's bytes after construction.
/// - default(SpkiPin) (or any other route around the constructor) is never
///   silently treated as a valid empty/zero pin — every member throws until
///   it's constructed properly, so an uninitialized pin fails loudly at
///   first use instead of comparing as some seemingly-valid value.
/// </summary>
[JsonConverter(typeof(SpkiPinJsonConverter))]
public readonly struct SpkiPin : IEquatable<SpkiPin>
{
    const int Sha256Length = 32;

    readonly byte[]? _bytes;

    public SpkiPin(byte[] bytes)
    {
        if (bytes.Length != Sha256Length)
        {
            throw new ArgumentException(
                $"A SPKI pin must be a {Sha256Length}-byte SHA-256 hash, got {bytes.Length}.", nameof(bytes));
        }
        _bytes = (byte[])bytes.Clone();
    }

    public ReadOnlySpan<byte> Bytes => RequireInitialized();

    public bool Equals(SpkiPin other) => RequireInitialized().SequenceEqual(other.RequireInitialized());

    public override bool Equals(object? obj) => obj is SpkiPin other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(RequireInitialized());
        return hash.ToHashCode();
    }

    public override string ToString() => Convert.ToHexString(RequireInitialized());

    public static bool operator ==(SpkiPin left, SpkiPin right) => left.Equals(right);
    public static bool operator !=(SpkiPin left, SpkiPin right) => !left.Equals(right);

    byte[] RequireInitialized() => _bytes
        ?? throw new InvalidOperationException(
            "SpkiPin was never assigned a value (e.g. default(SpkiPin)) — it must be constructed with a 32-byte SHA-256 hash.");
}
