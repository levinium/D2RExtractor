using D2RExtractor.Models;

namespace D2RExtractor.Cli;

/// <summary>
/// Argument parsing. Hand-rolled deliberately: the surface is small enough that a package would
/// cost more than it saves, and the port promised no new dependencies.
/// </summary>
internal sealed class CommandLine
{
    public string Command { get; private init; } = "help";
    public string? Target { get; private init; }
    public bool Yes { get; private init; }
    public bool Verify { get; private init; }
    public bool NoInternational { get; private init; }
    public string? International { get; private init; }
    public string? Error { get; private init; }

    private static readonly string[] Known =
        { "add", "list", "forget", "status", "extract", "update", "undo", "help", "--help", "-h", "--version" };

    public static CommandLine Parse(string[] args)
    {
        if (args.Length == 0) return new CommandLine { Command = "help" };

        string command = args[0].ToLowerInvariant();
        if (!Known.Contains(command))
            return new CommandLine { Command = command, Error = $"Unknown command '{args[0]}'." };

        string? target = null, international = null;
        bool yes = false, verify = false, noIntl = false;

        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--yes" or "-y": yes = true; break;
                case "--verify": verify = true; break;
                case "--no-international": noIntl = true; break;
                case "--international":
                    international = Language(i + 1 < args.Length ? args[++i] : null, out string? error);
                    if (error != null) return new CommandLine { Command = command, Error = error };
                    break;
                default:
                    if (a.StartsWith("--international=", StringComparison.Ordinal))
                    {
                        international = Language(a["--international=".Length..], out error);
                        if (error != null) return new CommandLine { Command = command, Error = error };
                        break;
                    }
                    if (a.StartsWith('-'))
                        return new CommandLine { Command = command, Error = $"Unknown option '{a}'." };
                    if (target != null)
                        return new CommandLine { Command = command, Error = "More than one target given." };
                    target = a;
                    break;
            }
        }

        return new CommandLine
        {
            Command = command, Target = target, Yes = yes,
            Verify = verify, International = international, NoInternational = noIntl,
        };
    }

    /// <summary>
    /// Returns the canonical code (deDE) for a language given in any casing (dede), or an error.
    /// Checking here stops a missing value from swallowing the next option, as in
    /// <c>--international --verify</c>, and stops an unknown code from reaching an extraction.
    /// </summary>
    private static string? Language(string? value, out string? error)
    {
        foreach (var (code, _) in AppPreferences.AvailableLanguages)
        {
            if (string.Equals(code, value, StringComparison.OrdinalIgnoreCase))
            {
                error = null;
                return code;
            }
        }

        string known = string.Join(", ", AppPreferences.AvailableLanguages.Select(l => l.Code.ToLowerInvariant()));
        error = string.IsNullOrEmpty(value) || value.StartsWith('-')
            ? $"--international needs a language code: {known}."
            : $"Unknown language '{value}'. Use one of: {known}.";
        return null;
    }
}
