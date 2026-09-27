using HtmlAgilityPack;
using static DocumentHelper.Lookup.Http;

namespace DocumentHelper.Lookup;

/// <summary>
/// Cambridge Dictionary, English–Turkish (dictionary.cambridge.org). English words only.
/// Plain HTTP works here; each "def-block" holds an English definition, its Turkish translation and examples.
/// </summary>
public sealed class CambridgeSource : ITranslationSource
{
    private readonly AsyncCache<List<Meaning>?> _phrasalCache = new();

    public string Name => "Cambridge Dictionary";

    public string GetPageUrl(string word) =>
        "https://dictionary.cambridge.org/search/direct/?datasetsearch=english-turkish&q=" + Uri.EscapeDataString(word);

    public async Task<SourceResult> LookupAsync(string word, CancellationToken cancellationToken)
    {
        // The "search/direct" endpoint resolves inflections (houses -> house) and phrases (look up -> look-up).
        string url = GetPageUrl(word);
        var (document, finalUri) = await GetPageAsync(url, cancellationToken);

        if (!IsEntryPage(finalUri))
            return SourceResult.Empty(Name, url, "No entry found (Cambridge only covers English words).");

        var meanings = Parse(document);
        return meanings.Count == 0
            ? SourceResult.Empty(Name, finalUri.AbsoluteUri, "No Turkish translation found.")
            : new SourceResult(Name, finalUri.AbsoluteUri, meanings, null);
    }

    /// <summary>
    /// The English–Turkish entry for a phrasal verb such as "look after", or null if Cambridge has none.
    /// Results are cached.
    /// </summary>
    public Task<List<Meaning>?> LookupPhrasalVerbAsync(string phrase) =>
        _phrasalCache.GetOrAdd(PhrasalText.Normalize(phrase), async () =>
        {
            string normalized = PhrasalText.Normalize(phrase);
            var (document, finalUri) = await GetPageAsync(GetPageUrl(normalized), CancellationToken.None);
            // Unknown phrases redirect to a spelling page or to a different entry; only accept the phrase itself.
            if (!IsEntryPage(finalUri) || !PhrasalText.UrlMatches(finalUri, normalized)) return null;
            var meanings = Parse(document);
            return meanings.Count > 0 ? meanings : null;
        });

    private static bool IsEntryPage(Uri uri) =>
        uri.AbsolutePath.Contains("/dictionary/english-turkish/", StringComparison.OrdinalIgnoreCase);

    internal static List<Meaning> Parse(HtmlDocument doc)
    {
        var meanings = new List<Meaning>();
        var seen = new HashSet<string>();

        foreach (var entry in doc.DocumentNode.SelectNodes($"//div[{HasClass("entry-body__el")}]") ?? Enumerable.Empty<HtmlNode>())
        {
            string? headword = Text(entry.SelectSingleNode($".//span[{HasClass("hw")}]"));
            string? pos = Text(entry.SelectSingleNode($".//span[{HasClass("pos")}]"));

            foreach (var block in entry.SelectNodes($".//div[{HasClass("def-block")}]") ?? Enumerable.Empty<HtmlNode>())
            {
                // The sense's translation, sometimes wrapped in a "trans-block"; example sentences can carry
                // their own translations, which must not be mistaken for the meaning.
                var trans = block.SelectNodes(
                    $"./div[{HasClass("def-body")}]//span[{HasClass("trans")} and @lang='tr'][not(ancestor::div[{HasClass("examp")}])]");
                if (trans == null) continue;

                string turkish = string.Join(", ", trans.Select(Text).Where(t => !string.IsNullOrEmpty(t)));
                if (turkish.Length == 0) continue;

                string? definition = Text(block.SelectSingleNode($".//div[{HasClass("def")}]"))?.TrimEnd(':', ' ');
                string? guideWord = Text(block.SelectSingleNode(
                    $"ancestor::div[{HasClass("sense-block")}][1]//span[{HasClass("sense-title")}]"));
                string? phrase = Text(block.SelectSingleNode(
                    $"ancestor::div[{HasClass("phrase-block")}][1]//span[{HasClass("phrase-title")}]"));
                var examples = (block.SelectNodes($".//div[{HasClass("examp")}]/span[{HasClass("eg")}]") ?? Enumerable.Empty<HtmlNode>())
                    .Select(Text).OfType<string>().ToList();

                var context = new List<string>();
                if (phrase != null) context.Add(phrase);
                else if (headword != null) context.Add(headword);
                if (pos != null) context.Add(pos);
                if (guideWord != null) context.Add(guideWord);

                if (seen.Add(turkish + "|" + definition))
                    meanings.Add(new Meaning(turkish, string.Join(" · ", context), definition) { Examples = examples });
            }
        }
        return meanings;
    }
}
