using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using D2RExtractor.Services.Updates;

namespace D2RExtractor.Services;

/// <summary>
/// Finds out whether a newer release has been published.
/// <para>
/// The whole of the network side is here, and it is deliberately small: one GET
/// with no body, no query string, no identifier, and no record kept of who
/// asked. What goes out is an HTTP request for a public file. Nothing about the
/// machine, the game installs or where they live ever leaves.
/// </para>
/// </summary>
public sealed class UpdateService
{
    /// <summary>
    /// Long enough for a slow connection, short enough that nothing waits on it.
    /// Nothing blocks on this check, so the only cost of the timeout expiring is
    /// that the question goes unanswered until next time.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static readonly HttpClient Http = CreateClient();

    private readonly Func<CancellationToken, Task<string?>> _fetch;

    /// <summary>The release feed this build reads, or null if it was built without one.</summary>
    public static string? FeedUrl { get; } = ReadMetadata("UpdateFeedUrl");

    /// <summary>Where a person is sent to get the new version by hand.</summary>
    public static string? PageUrl { get; } = ReadMetadata("UpdatePageUrl");

    /// <summary>Whether this build can check at all.</summary>
    public static bool IsAvailable => !string.IsNullOrWhiteSpace(FeedUrl);

    /// <summary>This build's version, as the assembly reports it.</summary>
    public static string CurrentVersion { get; } = ReadVersion();

    public UpdateService()
    {
        _fetch = FetchAsync;
    }

    /// <summary>Takes the fetch as a delegate so the decision can be exercised without a network.</summary>
    public UpdateService(Func<CancellationToken, Task<string?>> fetch)
    {
        _fetch = fetch;
    }

    /// <summary>
    /// Asks once, and answers with what it found.
    /// </summary>
    /// <remarks>
    /// Never throws. Every way this can fail — offline, DNS, a rate limit, a
    /// proxy returning a login page, malformed JSON — is the same answer to the
    /// user, which is that the question could not be answered right now. A
    /// background version check has no business raising an exception into an app
    /// that may be forty minutes into writing 45 GB.
    /// </remarks>
    public async Task<UpdateVerdict> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await _fetch(ct).ConfigureAwait(false);
            return UpdateDecision.For(CurrentVersion, ParseRelease(json));
        }
        catch
        {
            return UpdateDecision.For(CurrentVersion, null);
        }
    }

    /// <summary>
    /// Pulls the fields that matter out of a release document.
    /// <para>
    /// Hand-read rather than deserialized into a type, because the response
    /// carries several dozen fields this app has no interest in and binding to
    /// them would make an unrelated change upstream into a parse failure here.
    /// </para>
    /// </summary>
    public static ReleaseInfo? ParseRelease(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            var root = doc.RootElement;

            // The tag is the version; the name is a title someone typed and may
            // be anything at all. The tag is tried first for that reason, but a
            // release with only a name is still worth reading.
            var tag = Text(root, "tag_name") ?? Text(root, "name");
            if (tag is null) return null;

            return new ReleaseInfo(
                tag,
                Text(root, "html_url"),
                Text(root, "body"),
                FindAsset(root),
                Flag(root, "draft"),
                Flag(root, "prerelease"));
        }
        catch (JsonException)
        {
            // A proxy or captive portal answering with HTML is the usual cause,
            // and it means exactly what a failed request means.
            return null;
        }
    }

    /// <summary>
    /// The zip to install, if the release has one.
    /// <para>
    /// Matched on the extension rather than the exact name, which has already
    /// changed once: releases up to 1.1.2 attached
    /// "D2RExtractor-Compiled-Standalone.zip" and everything since carries the
    /// version in the file name. Pinning the old name would have stopped the
    /// updater dead at the first release that renamed it.
    /// </para>
    /// </summary>
    private static ReleaseAsset? FindAsset(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = Text(asset, "name");
            var url = Text(asset, "browser_download_url");

            if (name is null || url is null) continue;
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;

            var size = asset.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number
                ? s.GetInt64()
                : 0L;

            return new ReleaseAsset(name, url, size, Text(asset, "digest"));
        }

        return null;
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool Flag(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private async Task<string?> FetchAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(FeedUrl)) return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        using var response = await Http
            .GetAsync(FeedUrl, HttpCompletionOption.ResponseContentRead, timeout.Token)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode) return null;

        return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
    }

    internal static HttpClient CreateClient()
    {
        // No overall HttpClient.Timeout: the same client downloads a 63 MB zip,
        // and a timeout that suits a metadata request would abort that partway
        // through on any ordinary connection. The metadata request brings its
        // own CancellationToken deadline instead.
        var client = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

        // GitHub refuses a request with no User-Agent outright, so this is
        // required rather than polite. It names the product and version and
        // nothing else — no machine, no user, no install id.
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("D2RExtractor", CurrentVersion));

        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        return client;
    }

    private static string ReadVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;

        return version is null
            ? "0.0.0"
            : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private static string? ReadMetadata(string key)
    {
        var value = typeof(UpdateService).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value;

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
