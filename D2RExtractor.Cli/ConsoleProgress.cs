using D2RExtractor.Services;

namespace D2RExtractor.Cli;

/// <summary>
/// Renders <see cref="ExtractionProgress"/> to the terminal: one line rewritten in place on a
/// TTY, a plain line every few seconds when redirected, so piping to a log or a systemd unit
/// does not produce megabytes of carriage returns.
/// </summary>
internal sealed class ConsoleProgress : IProgress<ExtractionProgress>, IDisposable
{
    private readonly bool _interactive = !Console.IsOutputRedirected;
    private readonly object _gate = new();
    private DateTime _lastDraw = DateTime.MinValue;
    private int _lastWidth;

    public void Report(ExtractionProgress p)
    {
        var interval = _interactive ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(5);

        lock (_gate)
        {
            if (DateTime.UtcNow - _lastDraw < interval) return;
            _lastDraw = DateTime.UtcNow;

            string line = p.IsEnumerating
                ? $"  scanning… {Trim(p.CurrentFile, 60)}"
                : $"  {Percent(p),6}  {p.FilesProcessed:N0}/{p.TotalFiles:N0} files  {Bytes(p.BytesProcessed)}  {Trim(p.CurrentFile, 40)}";

            if (_interactive)
            {
                Console.Write('\r' + line.PadRight(Math.Max(_lastWidth, line.Length)));
                _lastWidth = line.Length;
            }
            else
            {
                Console.WriteLine(line.TrimEnd());
            }
        }
    }

    /// <summary>Clears the in-place line so whatever prints next starts clean.</summary>
    public void Dispose()
    {
        if (_interactive && _lastWidth > 0)
            Console.Write('\r' + new string(' ', _lastWidth) + '\r');
        _lastWidth = 0;
    }

    private static string Percent(ExtractionProgress p) =>
        p.TotalFiles > 0 ? $"{p.FilesProcessed * 100.0 / p.TotalFiles:F1}%" : "—";

    private static string Trim(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        s = s.Replace('\\', '/');
        return s.Length <= max ? s : "…" + s[^(max - 1)..];
    }

    public static string Bytes(long n)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = n;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return $"{v:0.#} {units[u]}";
    }
}
