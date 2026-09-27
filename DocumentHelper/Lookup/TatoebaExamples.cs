using System.Text.Json;

namespace DocumentHelper.Lookup;

public sealed record ExampleSentence(string English, string? Turkish);

/// <summary>
/// Example sentences from Tatoeba (tatoeba.org), a curated, openly licensed collection of sentences and
/// human translations. Only English sentences that have a Turkish translation are requested. Tatoeba's
/// search matches inflected forms too ("look up" also finds "looked up").
/// </summary>
public sealed class TatoebaExamples
{
    private readonly AsyncCache<IReadOnlyList<ExampleSentence>> _cache = new();
    private readonly SemaphoreSlim _throttle = new(4); // Be polite: at most 4 requests at a time.

    public static string GetPageUrl(string phrase) =>
        "https://tatoeba.org/en/sentences/search?from=eng&to=tur&trans_filter=limit&trans_to=tur&query="
        + Uri.EscapeDataString($"\"{phrase}\"");

    /// <summary>Not cancellable on purpose: the result is cached and reused when the same word is looked up again.</summary>
    public Task<IReadOnlyList<ExampleSentence>> SearchAsync(string phrase, int max) =>
        _cache.GetOrAdd($"{max}|{phrase}", () => FetchAsync(phrase, max));

    private async Task<IReadOnlyList<ExampleSentence>> FetchAsync(string phrase, int max)
    {
        // Quotes make Tatoeba match the words as a phrase (important for phrasal verbs). The word-count
        // filter skips one-word sentences like "Look!" that show nothing about how the word is used.
        string url = "https://tatoeba.org/en/api_v0/search?from=eng&to=tur&trans_filter=limit&trans_to=tur"
                     + "&sort=relevance&word_count_min=5&word_count_max=18&query="
                     + Uri.EscapeDataString($"\"{phrase}\"");

        await _throttle.WaitAsync();
        string json;
        try
        {
            json = await Http.Client.GetStringAsync(url);
        }
        finally
        {
            _throttle.Release();
        }

        var sentences = new List<ExampleSentence>();
        using var doc = JsonDocument.Parse(json);
        foreach (var result in doc.RootElement.GetProperty("results").EnumerateArray())
        {
            string? english = result.GetProperty("text").GetString();
            if (string.IsNullOrWhiteSpace(english)) continue;
            sentences.Add(new ExampleSentence(english, FindTurkish(result)));
        }

        return sentences.Take(max).ToList();
    }

    /// <summary>"translations" is [directTranslations[], indirectTranslations[]]; prefer a direct one.</summary>
    private static string? FindTurkish(JsonElement result)
    {
        if (!result.TryGetProperty("translations", out var groups)) return null;
        foreach (var group in groups.EnumerateArray())
            foreach (var translation in group.EnumerateArray())
                if (translation.GetProperty("lang").GetString() == "tur")
                    return translation.GetProperty("text").GetString();
        return null;
    }
}
