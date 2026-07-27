using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Intercom.Pairing;

/// <summary>
/// Builds the canonical pairing transcript and derives the six-digit SAS from
/// it, exactly per docs/research/pairing-security.md ("First-contact pairing
/// ceremony", steps 2-3) and ADR-0002. Pure function of its inputs, no I/O, no
/// randomness of its own (the nonces are generated elsewhere via
/// <see cref="PairingNonce.Generate"/>) — this is what fixed-nonce/fixed-SPKI
/// test vectors pin down deterministically.
///
/// <para><b>Canonical transcript byte layout</b> (all multi-byte integers
/// big-endian; every field is fixed-width, so no length prefixes are
/// needed):</para>
/// <code>
/// [0..23)    Label            ASCII "Intercom.Pairing.SAS.v1" (23 bytes, constant)
/// [23..27)   ProtocolVersion  int32 (value 1 today — Hello.CurrentProtocolVersion)
///
/// -- lower-SPKI peer record (see ordering rule below) --
/// [27..59)   Spki(lower)      32-byte SHA-256 SPKI hash
/// [59..75)   Nonce(lower)     16-byte nonce
/// [75..91)   PeerId(lower)    16-byte GUID (Guid.TryWriteBytes layout, matching FrameCodec)
///
/// -- higher-SPKI peer record --
/// [91..123)  Spki(higher)     32-byte SHA-256 SPKI hash
/// [123..139) Nonce(higher)    16-byte nonce
/// [139..155) PeerId(higher)   16-byte GUID
/// </code>
/// <para>Total length: 155 bytes, always — never variable. "Lower"/"higher"
/// is decided by comparing the two records' 32-byte SPKI hashes with
/// unsigned lexicographic byte comparison (<see cref="ReadOnlySpan{T}.SequenceCompareTo"/>
/// over <c>byte</c>, the same comparison <c>Intercom.ControlChannel.TieBreak</c>
/// uses for a different purpose) — this is what makes both sides hash
/// identical bytes regardless of which one opened the connection or which
/// one is "local" in either side's own code. The two SPKI hashes are never
/// equal in practice (see TieBreak's identical reasoning); if they ever were,
/// this throws rather than silently picking a side.</para>
///
/// <para><b>SHA-256 and rejection sampling</b> (step 3): SHA-256 the 155-byte
/// transcript. Take the top 20 bits of the hash (the first two bytes plus the
/// top 4 bits of the third byte) as an unsigned integer in [0, 1048575]. If it
/// is less than 1,000,000, that is the code (zero-padded to 6 digits — never
/// reduced modulo, so every one of the 1,000,000 six-digit codes is equally
/// likely and no code is ever biased toward the low end the way a naive
/// <c>value % 1_000_000</c> would bias it). If the sampled value is
/// &gt;= 1,000,000 (roughly a 4.6% chance per draw), the hash is rehashed
/// (<c>hash = SHA-256(hash)</c>) and the same top-20-bits test is retried
/// against the new hash. This repeats until a value below 1,000,000 is found;
/// the probability of needing more than a handful of rounds is astronomically
/// small, but a hard cap (<see cref="MaxRejectionRounds"/>) guards against an
/// infinite loop should the impossible happen.</para>
/// </summary>
public static class PairingTranscript
{
    static readonly byte[] Label = Encoding.ASCII.GetBytes("Intercom.Pairing.SAS.v1");

    const int SpkiLength = 32;
    const int GuidLength = 16;
    const int PeerRecordLength = SpkiLength + PairingNonce.Length + GuidLength; // 64
    const int ProtocolVersionLength = sizeof(int);

    public const int TranscriptLength = 23 /* label */ + ProtocolVersionLength + PeerRecordLength + PeerRecordLength; // 155

    const int SixDigitRange = 1_000_000;
    const int RejectionBits = 20;

    /// <summary>Defensive-only: the probability of exhausting this many
    /// rejection-sampling rounds is negligible (roughly 0.0463^64), so
    /// reaching it in practice would indicate a bug, not bad luck.</summary>
    const int MaxRejectionRounds = 64;

    /// <summary>Builds the canonical 155-byte transcript from two peer
    /// records, ordering them lexicographically by SPKI hash regardless of
    /// which is "local"/"remote" to the caller — this is the entire reason
    /// both sides of a pairing ceremony compute the identical transcript
    /// (and therefore identical code) even though one dialed and one
    /// accepted.</summary>
    public static byte[] BuildTranscript(int protocolVersion, PairingPeerRecord a, PairingPeerRecord b)
    {
        var (lower, higher) = OrderBySpki(a, b);

        var transcript = new byte[TranscriptLength];
        var span = transcript.AsSpan();
        var offset = 0;

        Label.CopyTo(span[offset..]);
        offset += Label.Length;

        BinaryPrimitives.WriteInt32BigEndian(span.Slice(offset, ProtocolVersionLength), protocolVersion);
        offset += ProtocolVersionLength;

        offset = WritePeerRecord(span, offset, lower);
        offset = WritePeerRecord(span, offset, higher);

        System.Diagnostics.Debug.Assert(offset == TranscriptLength);
        return transcript;
    }

    /// <summary>Computes the six-digit SAS (formatted "000000".."999999",
    /// no separating space — display formatting to "NNN NNN" is the UI's
    /// job) for the given pair of peer records.</summary>
    public static string ComputeCode(int protocolVersion, PairingPeerRecord a, PairingPeerRecord b)
    {
        var transcript = BuildTranscript(protocolVersion, a, b);
        var hash = SHA256.HashData(transcript);
        var value = RejectionSampleSixDigit(hash);
        return value.ToString("D6");
    }

    /// <summary>Rejection-samples an unbiased value in [0, 999999] from a
    /// SHA-256 hash by repeatedly taking the top 20 bits and rehashing on
    /// rejection. Exposed internally so tests can pin exact hash-to-code
    /// vectors independently of transcript construction.</summary>
    internal static int RejectionSampleSixDigit(byte[] hash)
    {
        var current = hash;
        for (var round = 0; round < MaxRejectionRounds; round++)
        {
            var candidate = Top20Bits(current);
            if (candidate < SixDigitRange) return candidate;
            current = SHA256.HashData(current);
        }

        throw new InvalidOperationException(
            $"Rejection sampling failed to produce a value below {SixDigitRange} in {MaxRejectionRounds} rounds; " +
            "this should be cryptographically impossible and indicates a bug.");
    }

    /// <summary>The first 20 bits of the hash, read as an unsigned big-endian
    /// integer: byte[0] and byte[1] in full, plus the top 4 bits of byte[2].
    /// Range [0, 1048575].</summary>
    static int Top20Bits(byte[] hash) =>
        ((hash[0] << 16) | (hash[1] << 8) | hash[2]) >> (24 - RejectionBits);

    static (PairingPeerRecord Lower, PairingPeerRecord Higher) OrderBySpki(PairingPeerRecord a, PairingPeerRecord b)
    {
        var comparison = a.Spki.Bytes.SequenceCompareTo(b.Spki.Bytes);
        if (comparison == 0)
        {
            throw new InvalidOperationException(
                "Cannot build a pairing transcript: both peer records present the identical SPKI hash.");
        }
        return comparison < 0 ? (a, b) : (b, a);
    }

    static int WritePeerRecord(Span<byte> span, int offset, PairingPeerRecord record)
    {
        record.Spki.Bytes.CopyTo(span.Slice(offset, SpkiLength));
        offset += SpkiLength;

        record.Nonce.Bytes.CopyTo(span.Slice(offset, PairingNonce.Length));
        offset += PairingNonce.Length;

        record.PeerId.TryWriteBytes(span.Slice(offset, GuidLength));
        offset += GuidLength;

        return offset;
    }
}
