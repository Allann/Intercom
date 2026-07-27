using System.Text.Json.Serialization;

namespace Intercom.App.Identity;

/// <summary>
/// A validated, immutable SHA-256 SPKI pin (ADR-0002). Wrapping this instead
/// of a bare byte[] does two things: it rejects a malformed pin at
/// construction rather than at first comparison, and it gives value
/// (structural) equality — a bare byte[] on an ApprovedPeer record would
/// otherwise compare by reference, silently breaking peer-lookup equality
/// for two logically-identical pins loaded from separate deserializations.
/// </summary>
[JsonConverter(typeof(SpkiPinJsonConverter))]
public readonly struct SpkiPin : IEquatable<SpkiPin>
{
    const int Sha256Length = 32;

    readonly byte[] _bytes;

    public SpkiPin(byte[] bytes)
    {
        if (bytes.Length != Sha256Length)
        {
            throw new ArgumentException(
                $"A SPKI pin must be a {Sha256Length}-byte SHA-256 hash, got {bytes.Length}.", nameof(bytes));
        }
        _bytes = bytes;
    }

    public ReadOnlySpan<byte> Bytes => _bytes;

    public bool Equals(SpkiPin other) =>
        (_bytes ?? []).AsSpan().SequenceEqual(other._bytes ?? []);

    public override bool Equals(object? obj) => obj is SpkiPin other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(_bytes ?? []);
        return hash.ToHashCode();
    }

    public override string ToString() => _bytes is null ? "" : Convert.ToHexString(_bytes);

    public static bool operator ==(SpkiPin left, SpkiPin right) => left.Equals(right);
    public static bool operator !=(SpkiPin left, SpkiPin right) => !left.Equals(right);
}
