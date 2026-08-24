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
        var match = Pattern.Match(NormalizeTag(tag));
        if (!match.Success || !match.Groups[1].Success) return false;
        return TryParseMatch(match, out version);
    }

    static string NormalizeTag(string tag)
    {
        var value = tag.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) return value[1..];
        return value;
    }

    static bool TryParseMatch(Match match, out AppVersion version)
    {
        version = Zero;
        var parts = new int[4];
        for (var index = 0; index < parts.Length; index++)
            if (!TryPart(match, index + 1, out parts[index])) return false;
        version = new AppVersion(parts[0], parts[1], parts[2], parts[3]);
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
