using Windows.ApplicationModel;
using Intercom.Updates;

namespace Intercom.App.Updates;

/// <summary>
/// Real implementation of <see cref="IRunningAppVersionProvider"/> via
/// <c>Package.Current.Id.Version</c> — verified against Microsoft Learn's
/// PackageVersion struct reference: a value-type struct with four ushort
/// fields, Major/Minor/Build/Revision, the same shape
/// <see cref="AppVersion"/> mirrors.
///
/// <c>Package.Current</c> throws when the process has no package identity
/// (running unpackaged) — the same documented limitation
/// <c>StartupTaskService</c> already lives with for
/// <c>Windows.ApplicationModel.StartupTask</c>. Unlike that class, this one
/// catches it rather than letting it propagate: there is no "enable" action
/// here for the caller to retry, just a read, and per issue #31's "never
/// blocks or throws" acceptance criterion the update check must degrade to
/// "no notice" rather than fail loudly when run from the unpackaged dev
/// inner loop (docs/adr/0003, "Developer Mode... registers the loose build
/// output directly, no signing or trust step at all").
/// </summary>
public sealed class PackageRunningVersionProvider : IRunningAppVersionProvider
{
    public AppVersion? GetRunningVersion()
    {
        try
        {
            var version = Package.Current.Id.Version;
            return new AppVersion(version.Major, version.Minor, version.Build, version.Revision);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Package.Current.Id.Version unavailable: {ex.Message}");
            return null;
        }
    }
}
