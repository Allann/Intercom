namespace Intercom.Presence;

/// <summary>Coarse idle-age bucket transmitted on the wire
/// (docs/research/active-device-presence.md "Presence lease" — "Bucket the
/// idle age. Do not transmit exact Windows input ticks or exact keystroke
/// timestamps."). Omitted entirely from the lease when Unavailable (see
/// <see cref="PresenceLease.IdleAgeBucket"/> being nullable).</summary>
public enum IdleAgeBucket : byte
{
    UnderTwoMinutes = 0,
    TwoToTenMinutes = 1,
    OverTenMinutes = 2,
}

/// <summary>Buckets a raw idle-age <see cref="TimeSpan"/> into the coarse,
/// privacy-preserving categories the wire format allows.</summary>
public static class IdleAgeBucketing
{
    static readonly TimeSpan TwoMinutes = TimeSpan.FromMinutes(2);
    static readonly TimeSpan TenMinutes = TimeSpan.FromMinutes(10);

    public static IdleAgeBucket Bucket(TimeSpan idleAge) => idleAge switch
    {
        _ when idleAge < TwoMinutes => IdleAgeBucket.UnderTwoMinutes,
        _ when idleAge < TenMinutes => IdleAgeBucket.TwoToTenMinutes,
        _ => IdleAgeBucket.OverTenMinutes,
    };
}
