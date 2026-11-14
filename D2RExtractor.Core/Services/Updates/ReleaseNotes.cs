using System.Text;

namespace D2RExtractor.Services.Updates;

/// <summary>
/// Turns a release body into something worth reading in a WPF TextBlock.
/// <para>
/// GitHub release notes are Markdown, and the dialog shows them as plain text.
/// Left alone, the first line of this project's own notes reads
/// "- **Added incremental updates.** After a D2R patch…", where every marker is
/// noise to someone deciding whether to let the app replace itself.
/// </para>
/// <para>
/// This is deliberately not a Markdown parser. It removes the handful of
/// markers these notes actually use and leaves everything else exactly as
/// written — an unrecognized construct should survive as the author typed it,
/// not be mangled by a half-implemented renderer.
/// </para>
/// </summary>
public static class ReleaseNotes
{
    public static string ToPlainText(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;

        var result = new StringBuilder(body.Length);

        foreach (var rawLine in body.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimEnd();

            // Bullets, kept as bullets. The marker is matched with its trailing
            // space so a line that merely starts with a dash - a negative number,
            // or an em-dash aside - is left alone.
            var trimmed = line.TrimStart();
            var indent = line.Length - trimmed.Length;

            if (trimmed.StartsWith("- ", StringComparison.Ordinal)
                || trimmed.StartsWith("* ", StringComparison.Ordinal))
            {
                line = new string(' ', indent) + "• " + trimmed[2..];
            }
            else if (trimmed.StartsWith("> ", StringComparison.Ordinal))
            {
                line = new string(' ', indent) + trimmed[2..];
            }
            else if (trimmed.StartsWith('#'))
            {
                line = new string(' ', indent) + trimmed.TrimStart('#').TrimStart();
            }

            result.AppendLine(StripEmphasis(line));
        }

        return result.ToString().Trim();
    }

    /// <summary>
    /// Drops bold and italic markers, and the backticks around inline code.
    /// <para>
    /// Doubles are removed before singles, so "**bold**" does not leave a pair of
    /// stray asterisks behind.
    /// </para>
    /// </summary>
    private static string StripEmphasis(string line) =>
        line.Replace("**", string.Empty)
            .Replace("__", string.Empty)
            .Replace("`", string.Empty);
}
