using System.Security.Cryptography;

namespace Intercom.Pairing;

/// <summary>
/// A fresh 128-bit random pairing nonce (docs/research/pairing-security.md
/// "First-contact pairing ceremony" step 1), generated with
/// <see cref="RandomNumberGenerator"/> — never <see cref="Random"/> — so an
/// attacker cannot precompute a certificate that produces a chosen
/// verification code. One nonce is generated fresh per ceremony attempt on
/// each side; it is never reused across attempts.
///
/// Mirrors <c>Intercom.Identity.SpkiPin</c>'s shape (defensive copy on
/// construction, value equality, loud failure on an uninitialized default
/// value) for the same reasons: this wraps a fixed-length security-sensitive
/// byte value that must never silently compare as valid when uninitialized.
/// </summary>
public readonly struct PairingNonce : IEquatable<PairingNonce>
{
    /// <summary>128 bits, per docs/research/pairing-security.md.</summary>
    public const int Length = 16;

    readonly byte[]? _bytes;

    public PairingNonce(byte[] bytes)
    {
        if (bytes.Length != Length)
        {
            throw new ArgumentException(
                $"A pairing nonce must be exactly {Length} bytes, got {bytes.Length}.", nameof(bytes));
        }
        _bytes = (byte[])bytes.Clone();
    }

    public ReadOnlySpan<byte> Bytes => RequireInitialized();

    /// <summary>Generates a fresh cryptographically strong nonce.</summary>
    public static PairingNonce Generate() => new(RandomNumberGenerator.GetBytes(Length));

    public bool Equals(PairingNonce other) => RequireInitialized().SequenceEqual(other.RequireInitialized());

    public override bool Equals(object? obj) => obj is PairingNonce other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(RequireInitialized());
        return hash.ToHashCode();
    }

    public override string ToString() => Convert.ToHexString(RequireInitialized());

    public static bool operator ==(PairingNonce left, PairingNonce right) => left.Equals(right);
    public static bool operator !=(PairingNonce left, PairingNonce right) => !left.Equals(right);

    byte[] RequireInitialized() => _bytes
        ?? throw new InvalidOperationException(
            "PairingNonce was never assigned a value (e.g. default(PairingNonce)) — it must be constructed with a 16-byte value.");
}
