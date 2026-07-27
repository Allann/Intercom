namespace Intercom.Updates;

/// <summary>
/// Orchestrates issue #31's manual update check: reads the running app's own
/// version, asks <see cref="IUpdateVersionSource"/> for the latest published
/// release, and decides whether a passive "update available" notice should
/// be shown — never blocking, never throwing, and never re-nagging for a
/// version the user already dismissed (unless a newer one has since shipped).
/// Mirrors <c>Intercom.Lifecycle.AppLifecycle</c>'s "small seams, orchestration
/// stays testable" shape, and <c>CrashMarker</c>'s "silent local state,
/// surfaced as a passive notice on next launch" precedent.
/// </summary>
public sealed class UpdateChecker
{
    readonly IUpdateVersionSource _versionSource;
    readonly IRunningAppVersionProvider _runningVersionProvider;
    readonly UpdateNoticeStore _noticeStore;

    public UpdateChecker(
        IUpdateVersionSource versionSource,
        IRunningAppVersionProvider runningVersionProvider,
        UpdateNoticeStore noticeStore)
    {
        _versionSource = versionSource;
        _runningVersionProvider = runningVersionProvider;
        _noticeStore = noticeStore;
    }

    /// <summary>
    /// Runs the full check. Returns a notice only when there is a genuinely
    /// newer, not-yet-dismissed release; returns null for every other case,
    /// including every failure mode (unreadable running version, unreachable
    /// source, malformed tag) — this method is guaranteed not to throw, so
    /// callers can fire-and-forget it without a surrounding try/catch.
    /// </summary>
    public async Task<UpdateAvailableNotice?> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var running = _runningVersionProvider.GetRunningVersion();
            if (running is null) return null;

            var latest = await _versionSource.GetLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
            if (latest is null) return null;

            if (!AppVersion.TryParse(latest.TagName, out var latestVersion)) return null;
            if (latestVersion <= running.Value) return null;

            var dismissed = _noticeStore.GetDismissedVersion();
            if (dismissed is not null && latestVersion <= dismissed.Value) return null;

            return new UpdateAvailableNotice(latestVersion, latest.HtmlUrl);
        }
        catch (Exception ex)
        {
            // Defense in depth on top of IUpdateVersionSource/
            // IRunningAppVersionProvider's own "never throws" contracts —
            // this is the last line before a fire-and-forget caller, so
            // nothing from here should ever become an unhandled exception.
            System.Diagnostics.Debug.WriteLine($"Update check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Call when the user dismisses the notice — persists so the
    /// same version doesn't reappear on the next launch, but a newer one
    /// still will.</summary>
    public void Dismiss(AppVersion version) => _noticeStore.MarkDismissed(version);
}

/// <summary>A newer release than the one currently running, ready to surface
/// as a passive, dismissable notice. <see cref="ReleaseUrl"/> is the
/// release's GitHub page — the "manual update" action is opening this in the
/// browser and re-running the newly downloaded, identically-signed MSIX;
/// this app never downloads or installs anything itself (ADR-0003).</summary>
public sealed record UpdateAvailableNotice(AppVersion Version, string ReleaseUrl);
