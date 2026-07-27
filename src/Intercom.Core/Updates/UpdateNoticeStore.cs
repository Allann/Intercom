namespace Intercom.Updates;

/// <summary>
/// Remembers "the user already dismissed the update notice for version X"
/// locally, the same small-marker-file persistence model as
/// <c>Intercom.Diagnostics.CrashMarker</c>: one plain-text file holding just
/// enough state, written on the dismiss action and read back at the next
/// check. Deliberately stores the dismissed VERSION rather than a boolean —
/// per issue #31's acceptance criterion, dismissing must not suppress the
/// notice forever, only until a newer version than the dismissed one shows
/// up (<see cref="UpdateChecker"/> is what compares against it).
/// </summary>
public sealed class UpdateNoticeStore
{
    readonly string _markerPath;

    public UpdateNoticeStore(string? appDataDirectory = null)
    {
        var dir = appDataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Intercom");
        Directory.CreateDirectory(dir);
        _markerPath = Path.Combine(dir, "update-dismissed.marker");
    }

    /// <summary>The last version the user explicitly dismissed the notice
    /// for, or null if none was ever dismissed or the marker is missing/
    /// unreadable/unparseable (treated the same as "never dismissed" —
    /// never throws).</summary>
    public AppVersion? GetDismissedVersion()
    {
        try
        {
            if (!File.Exists(_markerPath)) return null;
            var text = File.ReadAllText(_markerPath);
            return AppVersion.TryParse(text, out var version) ? version : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Call when the user dismisses the notice for a specific
    /// version (e.g. clicking the InfoBar's close button).</summary>
    public void MarkDismissed(AppVersion version) => File.WriteAllText(_markerPath, version.ToString());
}
