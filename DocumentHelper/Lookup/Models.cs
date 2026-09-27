namespace DocumentHelper.Lookup;

/// <summary>One Turkish meaning (possibly several synonyms) with the context it applies to.</summary>
/// <param name="Turkish">Turkish equivalent(s), e.g. "ev, konut".</param>
/// <param name="Context">What the meaning refers to, e.g. "noun · BUILDING" or "house (n.) · General".</param>
/// <param name="Definition">Optional source-language definition or explanation.</param>
public sealed record Meaning(string Turkish, string? Context, string? Definition)
{
    /// <summary>English example sentences for this meaning, if the source has them.</summary>
    public IReadOnlyList<string> Examples { get; init; } = [];
}

/// <summary>A phrasal verb built on the looked-up word, e.g. "look after" → "bakmak, ilgilenmek".</summary>
public sealed record PhrasalVerb(string Phrase, string Turkish);

public sealed record SourceResult(string SourceName, string? Url, IReadOnlyList<Meaning> Meanings, string? Message)
{
    public IReadOnlyList<PhrasalVerb> PhrasalVerbs { get; init; } = [];

    public static SourceResult Empty(string source, string? url, string message) => new(source, url, [], message);
}

public interface ITranslationSource
{
    string Name { get; }

    /// <summary>Public page for the word, shown as "open in browser".</summary>
    string GetPageUrl(string word);

    Task<SourceResult> LookupAsync(string word, CancellationToken cancellationToken);
}

/// <summary>One sense of a phrasal verb: an English definition and English example sentences.</summary>
public sealed record PhrasalSense(string? Definition, IReadOnlyList<string> Examples);

/// <summary>A phrasal verb listed by Oxford for a word, with the link to its Oxford entry.</summary>
public sealed record OxfordPhrasalLink(string Phrase, string Url);

/// <param name="Pattern">How the phrasal verb is used, e.g. "look after somebody/something".</param>
/// <param name="Senses">Definitions with example sentences.</param>
/// <param name="Url">The Oxford entry page.</param>
public sealed record OxfordPhrasalEntry(string? Pattern, IReadOnlyList<PhrasalSense> Senses, string Url);
