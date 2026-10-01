using System.IO;

namespace D2RExtractor.Models;

/// <summary>
/// The rules about where destinations may point.
///
/// <para>
/// Pulled out of the destinations window so they can be exercised directly. They are not a dialog's
/// business anyway: "these two folders overlap" is true whether or not anyone is looking at a
/// window, and it is the kind of rule that is cheap to get subtly wrong — <c>C:\D2R</c> and
/// <c>C:\D2RMods</c> share a prefix as strings and share nothing as folders.
/// </para>
/// </summary>
public static class DestinationPaths
{
    /// <summary>
    /// A path in the one form comparisons can use: absolute, no trailing separator.
    /// Returns null for anything that cannot be resolved.
    /// </summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch
        {
            // A malformed path, a bad drive letter, or something too long. Not a destination.
            return null;
        }
    }

    /// <summary>Whether two paths name the same folder.</summary>
    public static bool AreSame(string? a, string? b)
    {
        string? left = Normalize(a);
        string? right = Normalize(b);

        return left is not null && right is not null
            && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether <paramref name="path"/> sits inside <paramref name="maybeParent"/>.
    /// </summary>
    /// <remarks>
    /// The separator in the comparison is what makes this correct rather than nearly correct. A
    /// plain <c>StartsWith</c> says <c>C:\D2RMods</c> is inside <c>C:\D2R</c>, which would refuse a
    /// perfectly good destination; requiring the separator means only a real child matches.
    /// </remarks>
    public static bool IsUnder(string? path, string? maybeParent)
    {
        string? child = Normalize(path);
        string? parent = Normalize(maybeParent);

        if (child is null || parent is null) return false;

        return child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether two destinations would tread on each other — the same folder, or one inside the
    /// other.
    /// </summary>
    /// <remarks>
    /// Nesting has to be refused, not merely discouraged. Each destination's update treats any file
    /// under its tree that the archives do not contain as a leftover from an old patch and deletes
    /// it. Nest two and each one's update deletes the other's extraction.
    /// </remarks>
    public static bool Overlap(string? a, string? b) =>
        AreSame(a, b) || IsUnder(a, b) || IsUnder(b, a);
}
