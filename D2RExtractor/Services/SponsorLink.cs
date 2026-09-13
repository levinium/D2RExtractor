using System.Globalization;
using System.Reflection;

namespace D2RExtractor.Services;

/// <summary>
/// Where "Support D2R File Extractor" sends someone, and how much it says.
/// <para>
/// The destination is a build property rather than a constant, and it is empty
/// in the repository. A funding handle is not a secret — it exists to be given
/// out — but it identifies a person, and a fork built from source must not
/// quietly solicit donations to whoever wrote the code. With nothing set the
/// app has no donate button at all.
/// </para>
/// <para>
/// The template decides whether an amount can be asked for. Some destinations
/// take one in the URL and open ready to go; others choose the amount on their
/// own page. Offering a picker to the second kind would be a lie — you would
/// choose $5 and land somewhere that had never heard of it — so the presence of
/// an <c>{amount}</c> placeholder is what turns the picker on.
/// </para>
/// </summary>
public static class SponsorLink
{
    /// <summary>The placeholder a destination uses to say it accepts an amount.</summary>
    public const string AmountToken = "{amount}";

    /// <summary>
    /// What the picker offers. Small enough that nobody has to think about it,
    /// and topped out well before the point where a donation feels like a
    /// purchase that ought to come with something.
    /// </summary>
    public static IReadOnlyList<decimal> Suggested { get; } = [3m, 5m, 10m, 25m];

    /// <summary>
    /// Chosen for you, so the quickest path through the dialog is one click on
    /// a sum nobody regrets.
    /// </summary>
    public const decimal Default = 5m;

    /// <summary>The largest an "other" amount may be, as a guard against a slip.</summary>
    public const decimal Most = 1000m;

    /// <summary>
    /// The destination this build was published with, or null — which is the
    /// case for every build made from a clean checkout.
    /// </summary>
    public static string? Template { get; } = ReadTemplate();

    /// <summary>Whether there is anything to show a button for.</summary>
    public static bool IsOffered => !string.IsNullOrWhiteSpace(Template);

    /// <summary>Whether this destination understands an amount.</summary>
    public static bool CarriesAmount(string? template) =>
        template?.Contains(AmountToken, StringComparison.Ordinal) == true;

    /// <summary>
    /// The link to open, with the amount filled in where the destination takes
    /// one and left out where it does not.
    /// </summary>
    /// <remarks>
    /// Formatted invariantly and deliberately. A decimal rendered under a
    /// culture that writes "5,00" produces a URL the destination reads as a
    /// different number or as nothing at all, and the machine that would prove
    /// it is not usually the machine this is written on.
    /// </remarks>
    public static string For(string template, decimal amount)
    {
        if (!CarriesAmount(template)) return template;

        return template.Replace(AmountToken, Format(amount), StringComparison.Ordinal);
    }

    /// <summary>
    /// A sum as a person would write it: no trailing zeros on a whole number,
    /// two places when there are cents.
    /// </summary>
    public static string Format(decimal amount) =>
        amount == decimal.Truncate(amount)
            ? amount.ToString("0", CultureInfo.InvariantCulture)
            : amount.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>What the button says, ready to be read aloud.</summary>
    public static string Describe(decimal amount) => $"${Format(amount)}";

    /// <summary>
    /// Whether a typed amount is worth sending anyone to a payment page for.
    /// <para>
    /// Zero is not a donation, and the ceiling is there because a stray keypress
    /// should not open a page asking for four figures.
    /// </para>
    /// <para>
    /// Whole amounts only, which is not fussiness. The sponsor page's amount box
    /// is <c>pattern="[0-9]*"</c> and it TRUNCATES: handed 7.50 it renders 7, with
    /// no complaint and nothing to notice. So a dialog that accepted 7.50 would
    /// promise one number and open a page offering another, which is the single
    /// thing this dialog must never do.
    /// </para>
    /// </summary>
    public static bool IsUsable(decimal amount) =>
        amount > 0 && amount <= Most && amount == decimal.Truncate(amount);

    /// <summary>
    /// Parses what someone typed into the "Other" box, invariantly, returning
    /// null for anything not worth opening a payment page for.
    /// </summary>
    public static decimal? Parse(string? typed) =>
        decimal.TryParse(
            typed,
            NumberStyles.Currency,
            CultureInfo.InvariantCulture,
            out var amount) && IsUsable(amount)
            ? amount
            : null;

    /// <summary>
    /// Whether what was typed is a real sum that simply has cents in it. Worth
    /// telling someone about rather than leaving a dead button: the field looks
    /// like it ought to take 7.50, and being told why it does not beats guessing.
    /// </summary>
    public static bool HasCents(string? typed) =>
        decimal.TryParse(
            typed,
            NumberStyles.Currency,
            CultureInfo.InvariantCulture,
            out var amount)
        && amount > 0
        && amount != decimal.Truncate(amount);

    /// <summary>
    /// Reads what the build was published with. Assembly metadata rather than a
    /// file beside the exe: a single-file build has nowhere to put one.
    /// </summary>
    private static string? ReadTemplate()
    {
        var value = typeof(SponsorLink).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "SponsorUrl")?.Value;

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
