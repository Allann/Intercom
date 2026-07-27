using Intercom.Identity;
using Intercom.Pairing;
using Xunit;

namespace Intercom.App.Tests.Pairing;

/// <summary>
/// Fixed transcript test vectors for <see cref="PairingTranscript"/> — the
/// highest-risk, most security-sensitive code in issue #22 (a bug here
/// breaks the entire trust model). Every byte of the transcript below is
/// independently accounted for against docs/research/pairing-security.md's
/// exact layout, and the SHA-256 hash / rejection-sampled code were computed
/// out-of-band (PowerShell's <c>System.Security.Cryptography.SHA256</c>,
/// independently of this codebase's implementation) and pinned here as a
/// regression, per issue #22's "computed-and-pinned-as-a-regression-test"
/// acceptance option.
/// </summary>
public class PairingTranscriptTests
{
    const int ProtocolVersion = 1;

    // Every hex nibble in Spki/Nonce/PeerId below is deliberately a single
    // repeated digit (0x11, 0xAA, 0xEE, 0xBB) specifically so this test
    // vector is immune to any ambiguity about Guid.TryWriteBytes' internal
    // field byte order: a Guid built entirely from one repeated nibble
    // produces the identical 16-byte sequence regardless of which internal
    // layout is used to serialize it.
    static readonly byte[] SpkiLowerBytes = Repeat(0x11, 32);
    static readonly byte[] SpkiHigherBytes = Repeat(0xEE, 32);
    static readonly Guid PeerIdLower = Guid.Parse("11111111-1111-1111-1111-111111111111");
    static readonly Guid PeerIdHigher = Guid.Parse("22222222-2222-2222-2222-222222222222");

    static PairingPeerRecord LowerRecord(byte[] nonce) => new()
    {
        Spki = new SpkiPin(SpkiLowerBytes),
        Nonce = new PairingNonce(nonce),
        PeerId = PeerIdLower,
    };

    static PairingPeerRecord HigherRecord(byte[] nonce) => new()
    {
        Spki = new SpkiPin(SpkiHigherBytes),
        Nonce = new PairingNonce(nonce),
        PeerId = PeerIdHigher,
    };

    static byte[] Repeat(byte value, int count) => Enumerable.Repeat(value, count).ToArray();

    static byte[] HexToBytes(string hex) => Convert.FromHexString(hex);

    [Fact]
    public void BuildTranscript_MatchesExactPublishedByteLayout()
    {
        var lower = LowerRecord(Repeat(0xAA, 16));
        var higher = HigherRecord(Repeat(0xBB, 16));

        var transcript = PairingTranscript.BuildTranscript(ProtocolVersion, lower, higher);

        const string expectedHex =
            "496e746572636f6d2e50616972696e672e5341532e763100000001" + // label + protocol version
            "1111111111111111111111111111111111111111111111111111111111111111" + // Spki(lower), 32 bytes 0x11
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" + // Nonce(lower), 16 bytes 0xAA
            "11111111111111111111111111111111" + // PeerId(lower), 16 bytes 0x11
            "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee" + // Spki(higher), 32 bytes 0xEE
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" + // Nonce(higher), 16 bytes 0xBB
            "22222222222222222222222222222222"; // PeerId(higher), 16 bytes 0x22

        Assert.Equal(155, transcript.Length);
        Assert.Equal(HexToBytes(expectedHex), transcript);
    }

    [Fact]
    public void BuildTranscript_OrdersRecordsBySpkiHash_RegardlessOfArgumentOrder()
    {
        var lower = LowerRecord(Repeat(0xAA, 16));
        var higher = HigherRecord(Repeat(0xBB, 16));

        var lowerFirst = PairingTranscript.BuildTranscript(ProtocolVersion, lower, higher);
        var higherFirst = PairingTranscript.BuildTranscript(ProtocolVersion, higher, lower);

        Assert.Equal(lowerFirst, higherFirst);
    }

    [Fact]
    public void ComputeCode_MatchesIndependentlyComputedVector()
    {
        var lower = LowerRecord(Repeat(0xAA, 16));
        var higher = HigherRecord(Repeat(0xBB, 16));

        var code = PairingTranscript.ComputeCode(ProtocolVersion, lower, higher);

        // Independently verified: SHA-256 of the 155-byte transcript above is
        // 13d1bb8ea237d7887b0b1abc63d22d8ab8b9fa6df29eef55371f2db180f18b13;
        // its top 20 bits are 81179 (< 1,000,000, so no rejection round was
        // needed), formatted "D6" -> "081179".
        Assert.Equal("081179", code);
    }

    [Fact]
    public void ComputeCode_SwappedInitiatorResponderRoles_ProducesIdenticalCode()
    {
        // Simulates the two sides of a real ceremony: each side calls
        // ComputeCode with itself as "local" (first argument) and the other
        // as "remote" (second argument) — i.e. the arguments are swapped
        // between the two calls, exactly like two independent processes
        // that dialed vs. accepted the connection. Per docs/research/
        // pairing-security.md's own required test ("swapped initiator/
        // responder roles produce the same canonical transcript/code").
        var deviceA = LowerRecord(Repeat(0xAA, 16));
        var deviceB = HigherRecord(Repeat(0xBB, 16));

        var codeAsSeenByA = PairingTranscript.ComputeCode(ProtocolVersion, deviceA, deviceB);
        var codeAsSeenByB = PairingTranscript.ComputeCode(ProtocolVersion, deviceB, deviceA);

        Assert.Equal(codeAsSeenByA, codeAsSeenByB);
        Assert.Equal("081179", codeAsSeenByA);
    }

    [Fact]
    public void ComputeCode_DifferentNoncePair_ProducesDifferentCode()
    {
        // The testable essence of "a MITM/two-connection harness produces
        // different codes": two independently-negotiated connections
        // produce two independent nonce pairs even with the same claimed
        // peer identities, and the resulting transcripts (and codes) must
        // differ.
        var lowerA = LowerRecord(Repeat(0xAA, 16));
        var higherA = HigherRecord(Repeat(0xBB, 16));
        var lowerB = LowerRecord(Repeat(0xCC, 16));
        var higherB = HigherRecord(Repeat(0xDD, 16));

        var codeConnectionA = PairingTranscript.ComputeCode(ProtocolVersion, lowerA, higherA);
        var codeConnectionB = PairingTranscript.ComputeCode(ProtocolVersion, lowerB, higherB);

        // Independently verified: SHA-256 of the second (0xCC/0xDD-nonce)
        // transcript has top-20 bits 148301 -> "148301".
        Assert.Equal("081179", codeConnectionA);
        Assert.Equal("148301", codeConnectionB);
        Assert.NotEqual(codeConnectionA, codeConnectionB);
    }

    [Fact]
    public void BuildTranscript_IdenticalSpkiHashes_Throws()
    {
        var record = LowerRecord(Repeat(0xAA, 16));
        var sameSpkiDifferentNonce = new PairingPeerRecord { Spki = record.Spki, Nonce = new PairingNonce(Repeat(0xBB, 16)), PeerId = Guid.NewGuid() };

        Assert.Throws<InvalidOperationException>(() =>
            PairingTranscript.BuildTranscript(ProtocolVersion, record, sameSpkiDifferentNonce));
    }

    [Theory]
    [InlineData(0x000000, 0)]      // exactly 0 -> accepted immediately, code "000000"
    [InlineData(0x0F423F, 999999)] // 0x0F423F = 999999 -> the largest legal value, still accepted
    public void RejectionSampleSixDigit_AcceptsFirstRoundValuesInRange(int top20Value, int expected)
    {
        var hash = HashWithTop20(top20Value);

        var result = PairingTranscript.RejectionSampleSixDigit(hash);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void RejectionSampleSixDigit_RejectsOutOfRangeValue_AndRehashesToASecondRound()
    {
        // Independently verified: this 32-byte hash's top 20 bits are
        // 1017794 (>= 1,000,000, so round 1 is rejected). SHA-256 of these
        // same 32 bytes ("rehash") has top-20 bits 971953 (< 1,000,000, so
        // round 2 is accepted). This proves rejection sampling actually
        // rejects and retries rather than ever reducing modulo.
        var hash = HexToBytes("f87c2f2e411a8a4ff6d617e6d9b1fc0a37ebdb756e7a89ebcd41aa86f06d07db");

        var result = PairingTranscript.RejectionSampleSixDigit(hash);

        Assert.Equal(971953, result);
    }

    [Fact]
    public void RejectionSampleSixDigit_NeverReturnsAValueOutsideZeroToNineNineNineNineNineNine()
    {
        // Sweeps many independent SHA-256 outputs (chained hashing, not
        // pairing-specific) and asserts every sampled value is always a
        // legal six-digit value — a coarse statistical sanity check
        // alongside the exact pinned vectors above.
        var current = Repeat(0x01, 32);
        for (var i = 0; i < 500; i++)
        {
            current = System.Security.Cryptography.SHA256.HashData(current);
            var value = PairingTranscript.RejectionSampleSixDigit(current);
            Assert.InRange(value, 0, 999_999);
        }
    }

    /// <summary>Builds a 32-byte array whose top 20 bits equal
    /// <paramref name="top20Value"/> and whose remaining bits are zero — a
    /// synthetic hash purely for exercising the boundary of the
    /// rejection-sampling test itself, not a real SHA-256 output.</summary>
    static byte[] HashWithTop20(int top20Value)
    {
        var hash = new byte[32];
        var shifted = top20Value << 4; // undo the >> 4 used to extract the top 20 bits
        hash[0] = (byte)(shifted >> 16);
        hash[1] = (byte)(shifted >> 8);
        hash[2] = (byte)shifted;
        return hash;
    }
}
