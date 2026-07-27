using System.Text.RegularExpressions;

namespace Intercom.Updates;

/// <summary>
/// The same four-part Major.Minor.Build.Revision shape as the packaged app's
/// own <c>Windows.ApplicationModel.PackageVersion</c> (verified against
/// Microsoft Learn's PackageVersion struct reference: Major/Minor/Build/
/// Revision, in that order) — so a running package version and a parsed
/// GitHub release tag are directly comparable without a lossy conversion in
/// either direction.
/// </summary>
public readonly record struct AppVersion(int Major, int Minor, int Build, int Revision) : IComparable<AppVersion>
{
    public static readonly AppVersion Zero = new(0, 0, 0, 0);

    // Leading "v"/"V" is optional (GitHub release tags in this repo are
    // typically "v1.2.3"); each part after the first is optional and
    // defaults to 0 (mirrors PackageVersion's four required parts, but a
    // tag like "v1.2" is still meaningful); anything after the numeric
    // dotted run (prerelease/build metadata, e.g. "-beta.1", "+abc123") is
    // ignored rather than rejected.
    static readonly Regex Pattern = new(@"^(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:\.(\d+))?", RegexOptions.Compiled);

    public int CompareTo(AppVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Build.CompareTo(other.Build);
        if (c != 0) return c;
        return Revision.CompareTo(other.Revision);
    }

    public static bool operator >(AppVersion left, AppVersion right) => left.CompareTo(right) > 0;
    public static bool operator <(AppVersion left, AppVersion right) => left.CompareTo(right) < 0;
    public static bool operator >=(AppVersion left, AppVersion right) => left.CompareTo(right) >= 0;
    public static bool operator <=(AppVersion left, AppVersion right) => left.CompareTo(right) <= 0;

    public override string ToString() => $"{Major}.{Minor}.{Build}.{Revision}";

    /// <summary>
    /// Parses a version tag (typically a GitHub release's <c>tag_name</c>,
    /// but also used to round-trip <see cref="ToString"/>'s own output for
    /// the locally persisted "dismissed version" marker). Never throws —
    /// anything that doesn't start with at least one numeric component
    /// (empty, "latest", "not-a-version", a bare "v", a numeric part too
    /// large for <see cref="int"/>, etc.) returns false with
    /// <paramref name="version"/> set to <see cref="Zero"/>, so a malformed
    /// or unexpected remote value simply means "no notice" rather than a
    /// misparse that could cause a false "up to date" or an infinite nag.
    /// </summary>
    public static bool TryParse(string? tag, out AppVersion version)
    {
        version = Zero;
        if (string.IsNullOrWhiteSpace(tag)) return false;

        var s = tag.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s[1..];

        var match = Pattern.Match(s);
        if (!match.Success || !match.Groups[1].Success) return false;

        if (!TryPart(match, 1, out var major)) return false;
        if (!TryPart(match, 2, out var minor)) return false;
        if (!TryPart(match, 3, out var build)) return false;
        if (!TryPart(match, 4, out var revision)) return false;

        version = new AppVersion(major, minor, build, revision);
        return true;
    }

    static bool TryPart(Match match, int group, out int value)
    {
        value = 0;
        var g = match.Groups[group];
        if (!g.Success) return true; // missing trailing part defaults to 0
        return int.TryParse(g.Value, out value);
    }
}
