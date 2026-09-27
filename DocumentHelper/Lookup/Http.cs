using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using HtmlAgilityPack;

namespace DocumentHelper.Lookup;

/// <summary>Shared HttpClient for sites that can be read with plain HTTP requests.</summary>
internal static class Http
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> HostThrottles = new(StringComparer.OrdinalIgnoreCase);

    public static HttpClient Client { get; } = Create();

    /// <summary>
    /// Downloads and parses a page, following redirects. Returns the parsed document and the final URL
    /// (dictionary sites redirect searches to the entry they found). At most 3 requests run per site at once.
    /// </summary>
    public static async Task<(HtmlDocument Document, Uri FinalUri)> GetPageAsync(string url, CancellationToken ct)
    {
        var throttle = HostThrottles.GetOrAdd(new Uri(url).Host, _ => new SemaphoreSlim(3));
        await throttle.WaitAsync(ct);
        try
        {
            using var response = await Client.GetAsync(url, ct);
            var finalUri = response.RequestMessage?.RequestUri ?? new Uri(url);
            // 404 pages are still parsed; callers decide from the final URL whether an entry was found.
            if (response.StatusCode != HttpStatusCode.NotFound)
                response.EnsureSuccessStatusCode();

            var document = new HtmlDocument();
            document.LoadHtml(await response.Content.ReadAsStringAsync(ct));
            return (document, finalUri);
        }
        finally
        {
            throttle.Release();
        }
    }

    /// <summary>XPath predicate: element has CSS class <paramref name="cls"/>.</summary>
    public static string HasClass(string cls) =>
        $"contains(concat(' ', normalize-space(@class), ' '), ' {cls} ')";

    /// <summary>Visible text of a node with entities decoded and whitespace collapsed; null if empty.</summary>
    public static string? Text(HtmlNode? node)
    {
        if (node == null) return null;
        string text = string.Join(' ', WebUtility.HtmlDecode(node.InnerText)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length == 0 ? null : text;
    }

    private static HttpClient Create()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        return client;
    }
}

/// <summary>Caches async results by key; failed results are not kept, so they are retried next time.</summary>
internal sealed class AsyncCache<T>
{
    private readonly ConcurrentDictionary<string, Lazy<Task<T>>> _items = new(StringComparer.OrdinalIgnoreCase);

    public Task<T> GetOrAdd(string key, Func<Task<T>> factory)
    {
        var lazy = _items.GetOrAdd(key, _ => new Lazy<Task<T>>(factory));
        if (lazy.Value.IsFaulted || lazy.Value.IsCanceled)
        {
            _items.TryRemove(key, out _);
            lazy = _items.GetOrAdd(key, _ => new Lazy<Task<T>>(factory));
        }
        return lazy.Value;
    }
}

/// <summary>Helpers for phrasal verbs written in different styles by different dictionaries.</summary>
public static class PhrasalText
{
    // Object placeholders dictionaries put inside phrasal verbs ("look sth up", "look after somebody").
    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "sb", "sth", "sb's", "sb/sth", "someone", "somebody", "something", "someone's", "somebody's", "oneself",
        "one's", "yourself", "smb", "smth", "doing", "sth/doing",
    };

    /// <summary>"Look sth up" / "look (something) up" → "look up": the form used for lookups and de-duplication.</summary>
    public static string Normalize(string phrase)
    {
        var words = phrase.ToLowerInvariant()
            .Replace('(', ' ').Replace(')', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !Placeholders.Contains(w));
        return string.Join(' ', words);
    }

    /// <summary>"look after" → "look-after", the URL form used by Cambridge and Oxford.</summary>
    public static string Slug(string phrase) => Normalize(phrase).Replace(' ', '-');

    /// <summary>
    /// True if a dictionary's final URL is the entry for <paramref name="phrase"/>, e.g. ".../look-after",
    /// ".../look-after-sb-sth" or ".../look-after_1". Searches for unknown phrases redirect elsewhere, and
    /// ".../look-up-to-sb" is a different phrasal verb than "look up", so only placeholders may follow.
    /// </summary>
    public static bool UrlMatches(Uri finalUri, string phrase)
    {
        string last = finalUri.AbsolutePath.TrimEnd('/').Split('/')[^1].ToLowerInvariant();
        int underscore = last.LastIndexOf('_');
        if (underscore > 0 && last[(underscore + 1)..].All(char.IsDigit)) last = last[..underscore];

        string slug = Slug(phrase);
        if (last == slug) return true;
        if (!last.StartsWith(slug + "-", StringComparison.Ordinal)) return false;
        return last[(slug.Length + 1)..].Split('-').All(Placeholders.Contains);
    }
}
