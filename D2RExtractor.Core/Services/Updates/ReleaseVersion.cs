using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace D2RExtractor.Services.Updates;

/// <summary>
/// A released version, and whether one is newer than another.
/// <para>
/// This exists because the obvious comparison is wrong. Ordering release names
/// as text puts "1.10.0" before "1.9.0", which would tell everyone on 1.9.0
/// that they are up to date and then never mention another release again. The
/// numbers have to be compared as numbers.
/// </para>
/// <para>
/// Strictly a subset of semantic versioning: three numbers and an optional
/// pre-release suffix. Build metadata is parsed and then discarded, because
/// semver says it takes no part in ordering — two builds of the same version
/// are the same version, whatever commit each came from.
/// </para>
/// </summary>
public readonly record struct ReleaseVersion(int Major, int Minor, int Patch, string? PreRelease)
    : IComparable<ReleaseVersion>
{
    /// <summary>Whether this is a pre-release rather than a finished one.</summary>
    public bool IsPreRelease => !string.IsNullOrEmpty(PreRelease);

    /// <summary>
    /// Reads a version out of a release tag or name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This project's tags carry the product name: "D2RExtractor_v1.1.7",
    /// "D2RExtractor_1.1.2", and a release name of "D2RExtractor v1.1.7". So the
    /// version is taken from after the last underscore or space, and only then
    /// is a leading "v" stripped.
    /// </para>
    /// <para>
    /// The tempting shortcut — scan forward to the first digit — is a bug with
    /// this naming, and a dangerous one. The first digit in "D2RExtractor_v1.1.7"
    /// is the 2 in "D2R", which parses as version 2.0.0 and would tell every user
    /// forever that a major new release is waiting. Splitting on the separator
    /// cannot make that mistake.
    /// </para>
    /// <para>
    /// The very first release was tagged plain "D2RExtractor", with no version in
    /// it at all. That correctly fails to parse here, and an unparseable tag is
    /// reported as <see cref="UpdateOutcome.Unknown"/> rather than guessed at.
    /// </para>
    /// </remarks>
    public static bool TryParse([NotNullWhen(true)] string? text, out ReleaseVersion version)
    {
        version = default;

        if (string.IsNullOrWhiteSpace(text)) return false;

        var span = text.Trim();

        // Drop a product-name prefix: everything up to and including the last
        // underscore or space. Deliberately the LAST, so "D2R Extractor v1.1.7"
        // loses all of it rather than just the first word.
        var separator = span.LastIndexOfAny(['_', ' ']);
        if (separator >= 0) span = span[(separator + 1)..];

        if (span.Length > 0 && (span[0] == 'v' || span[0] == 'V')) span = span[1..];

        // Build metadata never affects ordering, so it is dropped here rather
        // than carried around as a field nothing is allowed to look at.
        var plus = span.IndexOf('+');
        if (plus >= 0) span = span[..plus];

        string? pre = null;
        var dash = span.IndexOf('-');
        if (dash >= 0)
        {
            pre = span[(dash + 1)..];
            span = span[..dash];
            if (pre.Length == 0) return false;
        }

        var parts = span.Split('.');
        if (parts.Length is < 1 or > 3) return false;

        // "1" and "1.2" are accepted as 1.0.0 and 1.2.0. A release named that way
        // is unambiguous about what it means, and refusing it would silently stop
        // the update check rather than visibly fail.
        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                return false;

            numbers[i] = n;
        }

        version = new ReleaseVersion(numbers[0], numbers[1], numbers[2], pre);
        return true;
    }

    /// <summary>Whether this version is one someone on <paramref name="other"/> should hear about.</summary>
    public bool IsNewerThan(ReleaseVersion other) => CompareTo(other) > 0;

    public int CompareTo(ReleaseVersion other)
    {
        if (Major != other.Major) return Major.CompareTo(other.Major);
        if (Minor != other.Minor) return Minor.CompareTo(other.Minor);
        if (Patch != other.Patch) return Patch.CompareTo(other.Patch);

        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    /// <summary>
    /// Pre-release ordering, per semver.
    /// <para>
    /// The rule that matters: a version WITH a pre-release suffix is older than
    /// the same version without one, because 1.1.0-rc1 is what comes before
    /// 1.1.0. Getting this backwards would offer people on the finished release
    /// a downgrade to the release candidate.
    /// </para>
    /// </summary>
    private static int ComparePreRelease(string? left, string? right)
    {
        var leftIsFinal = string.IsNullOrEmpty(left);
        var rightIsFinal = string.IsNullOrEmpty(right);

        if (leftIsFinal && rightIsFinal) return 0;
        if (leftIsFinal) return 1;
        if (rightIsFinal) return -1;

        var a = left!.Split('.');
        var b = right!.Split('.');

        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var aNumeric = int.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out var an);
            var bNumeric = int.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out var bn);

            // Numeric identifiers compare as numbers, and always rank below
            // alphanumeric ones — so "rc.10" beating "rc.2" is right.
            if (aNumeric && bNumeric)
            {
                if (an != bn) return an.CompareTo(bn);
                continue;
            }

            if (aNumeric) return -1;
            if (bNumeric) return 1;

            var text = string.CompareOrdinal(a[i], b[i]);
            if (text != 0) return text < 0 ? -1 : 1;
        }

        // Everything matched as far as the shorter one goes, so the one with
        // more identifiers is further along: "rc.1" precedes "rc.1.2".
        return a.Length.CompareTo(b.Length);
    }

    public override string ToString() =>
        IsPreRelease
            ? $"{Major}.{Minor}.{Patch}-{PreRelease}"
            : $"{Major}.{Minor}.{Patch}";
}
