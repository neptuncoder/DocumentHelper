namespace DocumentHelper.Lookup;

/// <param name="Turkish">Turkish equivalent, preferably from Cambridge, otherwise from Tureng.</param>
/// <param name="TurkishSource">"Cambridge" or "Tureng" (null if no Turkish equivalent was found).</param>
/// <param name="Pattern">Oxford's usage pattern, e.g. "look after somebody/something".</param>
/// <param name="Senses">English definitions with English example sentences (Oxford, else Cambridge).</param>
/// <param name="SensesSource">"Oxford" or "Cambridge".</param>
/// <param name="TranslatedExamples">Last resort when no dictionary has examples: Tatoeba sentences with Turkish.</param>
/// <param name="MoreUrl">Page with the full entry.</param>
public sealed record PhrasalVerbDetails(
    string? Turkish,
    string? TurkishSource,
    string? Pattern,
    IReadOnlyList<PhrasalSense> Senses,
    string? SensesSource,
    IReadOnlyList<ExampleSentence> TranslatedExamples,
    string? MoreUrl);

/// <summary>Combines Oxford, Cambridge, Tureng and Tatoeba into one description per phrasal verb.</summary>
public sealed class PhrasalVerbService
{
    private const int MaxSenses = 3;
    private const int MaxExamplesPerSense = 2;
    private const int MaxTranslatedExamples = 2;
    private const int MaxTurkishWords = 6;

    private readonly CambridgeSource _cambridge;
    private readonly OxfordSource _oxford;
    private readonly TatoebaExamples _tatoeba;

    public PhrasalVerbService(CambridgeSource cambridge, OxfordSource oxford, TatoebaExamples tatoeba)
    {
        _cambridge = cambridge;
        _oxford = oxford;
        _tatoeba = tatoeba;
    }

    /// <summary>Oxford's list of phrasal verbs for a word; empty if Oxford has none (or is unreachable).</summary>
    public async Task<List<OxfordPhrasalLink>> GetOxfordListAsync(string word)
    {
        try
        {
            return await _oxford.GetPhrasalVerbsAsync(word);
        }
        catch (Exception ex)
        {
            App.Log($"Oxford phrasal verb list for '{word}' failed: {ex.Message}");
            return [];
        }
    }

    /// <param name="phrase">The phrasal verb, e.g. "look after" or "look sth up".</param>
    /// <param name="oxfordUrl">Oxford entry link, when the phrasal verb came from Oxford's list.</param>
    /// <param name="turengTurkish">Tureng's Turkish meaning, used when Cambridge has none.</param>
    public async Task<PhrasalVerbDetails> GetDetailsAsync(string phrase, string? oxfordUrl, string? turengTurkish)
    {
        string normalized = PhrasalText.Normalize(phrase);
        var cambridgeTask = TryAsync(() => _cambridge.LookupPhrasalVerbAsync(normalized), "Cambridge", phrase);
        var oxfordTask = TryAsync(() => _oxford.GetPhrasalVerbAsync(normalized, oxfordUrl), "Oxford", phrase);
        await Task.WhenAll(cambridgeTask, oxfordTask);
        var cambridge = cambridgeTask.Result;
        var oxford = oxfordTask.Result;

        // Turkish: Cambridge first, Tureng as fallback.
        string? turkish = null, turkishSource = null;
        if (cambridge is { Count: > 0 })
        {
            turkish = JoinDistinct(cambridge.Select(m => m.Turkish));
            turkishSource = "Cambridge";
        }
        else if (!string.IsNullOrWhiteSpace(turengTurkish))
        {
            turkish = turengTurkish;
            turkishSource = "Tureng";
        }

        // English definitions and examples: Oxford first (made for learners), Cambridge as fallback.
        IReadOnlyList<PhrasalSense> senses = [];
        string? sensesSource = null;
        if (oxford is { Senses.Count: > 0 })
        {
            senses = Trim(oxford.Senses);
            sensesSource = "Oxford";
        }
        else if (cambridge is { Count: > 0 })
        {
            senses = Trim(cambridge.Select(m => new PhrasalSense(m.Definition, m.Examples)));
            sensesSource = "Cambridge";
        }

        IReadOnlyList<ExampleSentence> translated = [];
        if (!senses.Any(s => s.Examples.Count > 0))
        {
            try
            {
                translated = (await _tatoeba.SearchAsync(normalized, MaxTranslatedExamples)).ToList();
            }
            catch (Exception ex)
            {
                App.Log($"Tatoeba examples for '{phrase}' failed: {ex.Message}");
            }
        }

        string? moreUrl = oxford?.Url
            ?? (cambridge != null ? _cambridge.GetPageUrl(normalized) : null)
            ?? (translated.Count > 0 ? TatoebaExamples.GetPageUrl(normalized) : null);

        // Only worth showing when it adds something, e.g. "look after somebody/something".
        string? pattern = oxford?.Pattern is { } p && !p.Equals(phrase, StringComparison.OrdinalIgnoreCase) ? p : null;

        return new PhrasalVerbDetails(turkish, turkishSource, pattern, senses, sensesSource, translated, moreUrl);
    }

    private static List<PhrasalSense> Trim(IEnumerable<PhrasalSense> senses) => senses
        // Senses with examples are the useful ones; keep dictionary order otherwise.
        .OrderBy(s => s.Examples.Count > 0 ? 0 : 1)
        .Take(MaxSenses)
        .Select(s => s with { Examples = s.Examples.Take(MaxExamplesPerSense).ToList() })
        .ToList();

    /// <summary>Merges the Turkish of all senses ("bakmak, göz kulak olmak"), most common senses first.</summary>
    private static string JoinDistinct(IEnumerable<string> parts) =>
        string.Join(", ", parts
            .SelectMany(p => p.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(p => p.Trim('\'', '"', '‘', '’', ' '))
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxTurkishWords));

    private static async Task<T?> TryAsync<T>(Func<Task<T>> action, string source, string phrase)
    {
        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            App.Log($"{source} lookup for phrasal verb '{phrase}' failed: {ex.Message}");
            return default;
        }
    }
}
