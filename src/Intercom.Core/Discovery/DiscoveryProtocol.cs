namespace Intercom.Discovery;

/// <summary>
/// Constants for the `_intercom._tcp.local` DNS-SD service (ADR-0001,
/// docs/research/local-network-transport.md). Kept in one place so the
/// service type string and TXT schema version can't drift between the
/// register and browse sides.
/// </summary>
public static class DiscoveryProtocol
{
    /// <summary>DNS-SD service type, without the trailing ".local" domain —
    /// callers append that per the DnsServiceRegister/Browse contract.</summary>
    public const string ServiceType = "_intercom._tcp";

    /// <summary>mDNS multicast domain all lookups happen under.</summary>
    public const string Domain = "local";

    /// <summary>Bumped only if the TXT record schema itself changes (field
    /// added/removed/reinterpreted) — not on every app release. A peer with a
    /// version this build doesn't understand is still shown as visible
    /// (discovery is presentation, not trust) but the app can choose to
    /// flag it as "unknown protocol" rather than assume compatibility.</summary>
    public const int CurrentVersion = 1;

    /// <summary>TXT key for <see cref="CurrentVersion"/>.</summary>
    public const string TxtKeyVersion = "v";

    /// <summary>TXT key for the non-secret peer-ID hint (ADR-0002: this is
    /// LocalIdentity.PeerId, a random routing identifier — never the SPKI
    /// hash or any certificate material). Discovery TXT records must never
    /// carry contact names, DND state, or activity data.</summary>
    public const string TxtKeyPeerIdHint = "id";
}
