using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Intercom.Updates;

/// <summary>
/// Real implementation of <see cref="IUpdateVersionSource"/> against this
/// repo's own public GitHub Releases API
/// (`https://api.github.com/repos/Allann/Intercom/releases/latest`) — no
/// authentication required for a public repo's latest-release lookup. JSON
/// shape verified with a live unauthenticated call: the fields this app
/// needs are `tag_name` and `html_url`; a repo with no releases yet (true of
/// this one today) returns HTTP 404, which is treated the same as any other
/// unreachable/unusable response — a null result, not an exception.
///
/// Per ADR-0003 ("manual updates... never auto-downloads or auto-installs")
/// and issue #31's explicit acceptance criterion, this call must never block
/// startup and must never surface an error to the user: every failure mode
/// (network error, timeout, non-success status, malformed/unexpected JSON)
/// is caught here and turned into a null return, never an exception.
/// </summary>
public sealed class GitHubReleaseVersionSource : IUpdateVersionSource
{
    const string LatestReleaseUrl = "https://api.github.com/repos/Allann/Intercom/releases/latest";

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // GitHub's API rejects requests with no User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Intercom-App-UpdateCheck");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public async Task<LatestRelease?> GetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Http.GetAsync(LatestReleaseUrl, cancellationToken).ConfigureAwait(false);

            // Includes 404 (no releases published yet — true of this repo
            // today) and 403 (rate-limited) — both are "no answer right
            // now," not an error worth surfacing.
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var payload = await JsonSerializer.DeserializeAsync<ReleasePayload>(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(payload?.TagName) || string.IsNullOrWhiteSpace(payload?.HtmlUrl)) return null;

            return new LatestRelease(payload.TagName, payload.HtmlUrl);
        }
        catch (Exception ex)
        {
            // Broad catch is deliberate: network errors, timeouts, DNS
            // failures, malformed JSON, and anything else the underlying
            // HttpClient/JsonSerializer calls could throw must all collapse
            // to "no update info available right now," per this class's
            // never-blocks-never-surfaces-an-error contract.
            System.Diagnostics.Debug.WriteLine($"GitHub release check failed: {ex.Message}");
            return null;
        }
    }

    sealed class ReleasePayload
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }
    }
}
