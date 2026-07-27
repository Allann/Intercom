namespace Intercom.App.Diagnostics;

/// <summary>
/// Manual-relaunch-only crash recovery (ADR-0003): no watchdog, just visibility.
/// A marker file is written at startup and removed on clean shutdown; if it's
/// already present at the next startup, the previous run didn't exit cleanly.
/// </summary>
public sealed class CrashMarker
{
    readonly string _markerPath;

    public CrashMarker()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Intercom");
        Directory.CreateDirectory(dir);
        _markerPath = Path.Combine(dir, "running.marker");
    }

    /// <summary>Call once at startup, before doing anything else. Returns true if the
    /// previous run left the marker behind — i.e. closed unexpectedly.</summary>
    public bool ClosedUnexpectedlyLastTime()
    {
        var wasDirty = File.Exists(_markerPath);
        File.WriteAllText(_markerPath, DateTimeOffset.UtcNow.ToString("O"));
        return wasDirty;
    }

    /// <summary>Call on orderly shutdown (tray Quit), not on window close-to-tray.</summary>
    public void MarkCleanShutdown()
    {
        if (File.Exists(_markerPath)) File.Delete(_markerPath);
    }
}
