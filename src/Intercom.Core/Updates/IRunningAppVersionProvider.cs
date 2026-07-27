namespace Intercom.Updates;

/// <summary>
/// Thin seam over reading the currently running package's own version
/// (<c>Windows.ApplicationModel.Package.Current.Id.Version</c>) — the same
/// seam pattern as <see cref="IUpdateVersionSource"/>. The real
/// implementation (<c>Intercom.App.Updates.PackageRunningVersionProvider</c>)
/// depends on WinRT projections only available from the App project (mirrors
/// where <c>StartupTaskService</c> lives, for the same reason), so it can't
/// live in Intercom.Core alongside this interface.
/// </summary>
public interface IRunningAppVersionProvider
{
    /// <summary>Returns the running app's package version, or null if it
    /// can't be determined (e.g. running unpackaged during development —
    /// <c>Package.Current</c> throws in that case; see
    /// <c>PackageRunningVersionProvider</c>'s remarks). Must never throw.
    /// </summary>
    AppVersion? GetRunningVersion();
}
