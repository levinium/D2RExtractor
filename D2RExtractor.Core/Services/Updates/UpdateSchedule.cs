namespace D2RExtractor.Services.Updates;

/// <summary>
/// How often the app is allowed to go and look.
/// <para>
/// Separate from the check itself because "should I ask" and "what did the
/// answer mean" fail in different ways, and only one of them needs a network to
/// reproduce.
/// </para>
/// </summary>
public static class UpdateSchedule
{
    /// <summary>
    /// Once a day.
    /// <para>
    /// Releases arrive weeks apart, so checking more often buys nothing and
    /// spends someone else's bandwidth to learn the same answer. Checking less
    /// often means a fix can sit unnoticed for a week — which matters here,
    /// because the fix people are usually waiting for is the one that makes
    /// extraction work again after a game patch.
    /// </para>
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    /// <summary>Whether enough time has passed since the last look.</summary>
    /// <param name="lastCheckUtc">When the last check completed, or null if never.</param>
    /// <param name="nowUtc">The current time.</param>
    /// <remarks>
    /// A stored time in the FUTURE counts as due. It should be impossible, but
    /// it is exactly what a clock correction or a timezone-confused write leaves
    /// behind, and the naive comparison treats it as "checked recently" forever
    /// — a check that silently never runs again, on the machines least likely to
    /// notice. Treating it as due costs one request and repairs itself.
    /// </remarks>
    public static bool IsDue(DateTimeOffset? lastCheckUtc, DateTimeOffset nowUtc) =>
        IsDue(lastCheckUtc, nowUtc, Interval);

    /// <inheritdoc cref="IsDue(DateTimeOffset?, DateTimeOffset)"/>
    public static bool IsDue(DateTimeOffset? lastCheckUtc, DateTimeOffset nowUtc, TimeSpan interval)
    {
        if (lastCheckUtc is not { } last) return true;
        if (last > nowUtc) return true;

        return nowUtc - last >= interval;
    }
}
