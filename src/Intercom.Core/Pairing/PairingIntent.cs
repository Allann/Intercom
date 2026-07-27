namespace Intercom.Pairing;

/// <summary>The single byte of "pairing intent" carried in a
/// <c>PairingNonce</c> wire message (docs/research/pairing-security.md step
/// 1). One value today — this device wants to pair — but kept as an explicit
/// wire field (rather than implied) so a later intent (e.g. "re-pair after
/// identity change", explicitly out of scope for this ticket) can be added
/// without a wire-format break.</summary>
public enum PairingIntent : byte
{
    Pair = 1,
}
