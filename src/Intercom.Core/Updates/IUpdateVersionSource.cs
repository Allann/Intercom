namespace Intercom.Updates;

/// <summary>
/// Thin seam over the remote version source (issue #31: "a static version
/// source, e.g. a GitHub Releases API or a static JSON manifest") — the same
/// seam-interface pattern as <c>Intercom.Discovery.IDnsServiceDiscovery</c>/
/// <c>Intercom.Presence.IIdleTimeProvider</c>. All policy (version parsing,
/// comparison, dismiss persistence) lives in <see cref="UpdateChecker"/>,
/// which depends only on this interface, so it's fully unit-testable with a
/// hand-written fake. The real implementation
/// (<see cref="GitHubReleaseVersionSource"/>) hits a live network endpoint
/// and is deliberately not exercised by the unit tests — same reasoning as
/// <c>Win32DnsServiceDiscovery</c> being untestable in a normal CI sandbox.
/// </summary>
public interface IUpdateVersionSource
{
    /// <summary>
    /// Returns the latest published release, or null if the source is
    /// unreachable, rate-limited, has no releases yet, or otherwise can't
    /// produce a usable answer right now. Must never throw — implementations
    /// are responsible for catching their own I/O/parse failures and
    /// returning null instead, so a caller never needs a try/catch around
    /// this call to stay safe (<see cref="UpdateChecker"/> wraps it in one
    /// anyway, as defense in depth, but the contract itself is "never
    /// throws").
    /// </summary>
    Task<LatestRelease?> GetLatestReleaseAsync(CancellationToken cancellationToken);
}

/// <summary>The two fields of the GitHub Releases API response this app
/// actually needs, verified against a live call to
/// `https://api.github.com/repos/&lt;owner&gt;/&lt;repo&gt;/releases/latest`:
/// `tag_name` (e.g. "1.130.0" or "v1.2.3") and `html_url` (the release's
/// human-facing page, suitable for "open in browser").</summary>
public sealed record LatestRelease(string TagName, string HtmlUrl);
