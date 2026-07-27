namespace Intercom.Lifecycle;

/// <summary>What AppLifecycle.Start found out on this launch — enough for a
/// caller to decide whether to surface a crash or identity-loss notice,
/// without needing to know how any of it was detected.</summary>
public sealed record LaunchOutcome
{
    public required bool ClosedUnexpectedlyLastTime { get; init; }
    public required bool IdentityWasRegenerated { get; init; }
    public required bool RegistryWasReset { get; init; }
    public required bool PendingPairingsWereReset { get; init; }
}
