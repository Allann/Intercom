namespace Intercom.Lifecycle;

public enum StartupPreference
{
    Enabled,
    Disabled,
    DisabledByUser,
    DisabledByPolicy,
    Unknown,
}

/// <summary>Launch-at-sign-in, as far as AppLifecycle needs to know (ADR-0003:
/// enabled silently on first run, then always reflects real OS state).</summary>
public interface IStartupService
{
    Task<StartupPreference> EnableOnFirstRunAsync();
}
