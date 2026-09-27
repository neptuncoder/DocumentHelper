using HtmlAgilityPack;
using static DocumentHelper.Lookup.Http;

namespace DocumentHelper.Lookup;

/// <summary>
/// Oxford Learner's Dictionaries (oxfordlearnersdictionaries.com): the list of phrasal verbs for a word
/// (the "Phrasal Verbs" box of a verb entry) and English definitions with example sentences for each.
/// </summary>
public sealed class OxfordSource
{
    private const string BaseUrl = "https://www.oxfordlearnersdictionaries.com";
    private const int MaxEntriesToCheck = 3;

    private readonly AsyncCache<List<OxfordPhrasalLink>> _listCache = new();
    private readonly AsyncCache<OxfordPhrasalEntry?> _entryCache = new();

    public static string SearchUrl(string query) =>
        BaseUrl + "/search/english/direct/?q=" + Uri.EscapeDataString(query);

    /// <summary>Phrasal verbs Oxford lists for <paramref name="word"/> (e.g. look → look after, look ahead, …).</summary>
    public Task<List<OxfordPhrasalLink>> GetPhrasalVerbsAsync(string word) =>
        _listCache.GetOrAdd(word, async () =>
        {
            // The search resolves to the first entry, which may be the noun ("house") or an inflection
            // ("went"). The phrasal verbs are in the verb entry, so check a few related entries too.
            var (document, finalUri) = await GetPageAsync(SearchUrl(word), CancellationToken.None);
            if (!IsEntryPage(finalUri)) return [];

            var links = ReadPhrasalVerbLinks(document);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { WithoutQuery(finalUri) };
            foreach (string url in RelatedVerbEntries(document).Where(visited.Add).Take(MaxEntriesToCheck))
            {
                if (links.Count > 0) break;
                var (related, relatedUri) = await GetPageAsync(url, CancellationToken.None);
                if (IsEntryPage(relatedUri)) links = ReadPhrasalVerbLinks(related);
            }
            return links;
        });

    /// <summary>
    /// Usage pattern, definitions and examples for a phrasal verb, or null if Oxford has no entry for it.
    /// <paramref name="entryUrl"/> is the Oxford link when known; otherwise the phrase is searched and only
    /// accepted if Oxford has an entry for exactly that phrase.
    /// </summary>
    public Task<OxfordPhrasalEntry?> GetPhrasalVerbAsync(string phrase, string? entryUrl) =>
        _entryCache.GetOrAdd(PhrasalText.Normalize(phrase), async () =>
        {
            string url = entryUrl != null ? StripFragment(entryUrl) : SearchUrl(PhrasalText.Normalize(phrase));
            var (document, finalUri) = await GetPageAsync(url, CancellationToken.None);
            if (!IsEntryPage(finalUri)) return null;
            if (entryUrl == null && !PhrasalText.UrlMatches(finalUri, phrase)) return null;

            var entry = document.DocumentNode.SelectSingleNode($"(//div[{HasClass("entry")}])[1]");
            if (entry == null) return null;
            // "look after somebody/something"; several patterns are separated by "|".
            string? pattern = Text(entry.SelectSingleNode($".//span[{HasClass("pv")}]"))?.Replace(" | ", " / ");
            var senses = ReadSenses(entry);
            return senses.Count > 0 || pattern != null ? new OxfordPhrasalEntry(pattern, senses, finalUri.GetLeftPart(UriPartial.Path)) : null;
        });

    private static List<OxfordPhrasalLink> ReadPhrasalVerbLinks(HtmlDocument document)
    {
        // Pages can show phrasal-verb boxes of other words (e.g. in cross-references), so keep only the
        // phrasal verbs of this entry's headword.
        string? headword = Text(document.DocumentNode.SelectSingleNode($"(//h1[{HasClass("headword")}])[1]"))?.ToLowerInvariant();
        if (headword == null) return [];

        var links = new List<OxfordPhrasalLink>();
        var seenPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var anchors = document.DocumentNode.SelectNodes(
            $"//aside[{HasClass("phrasal_verb_links")}]//a[@href]") ?? Enumerable.Empty<HtmlNode>();
        foreach (var anchor in anchors)
        {
            string? phrase = Text(anchor.SelectSingleNode($".//span[{HasClass("xh")}]")) ?? Text(anchor);
            string href = anchor.GetAttributeValue("href", "");
            if (phrase == null || !href.StartsWith(BaseUrl, StringComparison.OrdinalIgnoreCase)) continue;
            string normalized = PhrasalText.Normalize(phrase);
            if (!normalized.StartsWith(headword + " ", StringComparison.Ordinal)) continue;
            // "look to to do" is Oxford's short name for a pattern of "look to" (the object words are dropped);
            // it repeats a word and duplicates the "look to" entry.
            var words = normalized.Split(' ');
            if (words.Distinct().Count() != words.Length) continue;
            // Variants such as "look to", "look to … for", "look to … to do" share one page; list it once.
            if (seenPages.Add(StripFragment(href)))
                links.Add(new OxfordPhrasalLink(phrase, href));
        }
        return links;
    }

    /// <summary>
    /// Other entries worth checking for phrasal verbs: "house verb" when we landed on "house noun", and for
    /// an inflection page ("went: past tense of go") the base form it links to.
    /// </summary>
    private static IEnumerable<string> RelatedVerbEntries(HtmlDocument document)
    {
        var root = document.DocumentNode;

        // Result list of the same headword, labelled like "house verb".
        foreach (var anchor in root.SelectNodes("//a[@href][span[contains(@class, 'arl1')]]") ?? Enumerable.Empty<HtmlNode>())
        {
            string label = Text(anchor) ?? "";
            if (label.EndsWith(" verb", StringComparison.OrdinalIgnoreCase) && !label.Contains("phrasal", StringComparison.OrdinalIgnoreCase))
                yield return StripFragment(anchor.GetAttributeValue("href", ""));
        }

        // Inflections ("went") have a single top-level sense that cross-references the base form ("go").
        // Only the entry's own sense list counts: idioms further down also use single senses with links.
        var baseForm = root.SelectSingleNode(
            $"(//div[{HasClass("entry")}])[1]/ol[{HasClass("sense_single")}]//a[{HasClass("Ref")}][@href]");
        if (baseForm != null)
            yield return StripFragment(baseForm.GetAttributeValue("href", ""));
    }

    /// <summary>Senses of the main entry only (the page can also show related entries further down).</summary>
    private static List<PhrasalSense> ReadSenses(HtmlNode entry)
    {
        var senses = new List<PhrasalSense>();
        foreach (var sense in entry.SelectNodes($".//li[{HasClass("sense")}]") ?? Enumerable.Empty<HtmlNode>())
        {
            string? definition = Text(sense.SelectSingleNode($".//span[{HasClass("def")}]"));
            // Direct example list only; "Extra examples" boxes are nested deeper and are less typical.
            var examples = (sense.SelectNodes($"./ul[{HasClass("examples")}]/li/span[{HasClass("x")}]") ?? Enumerable.Empty<HtmlNode>())
                .Select(Text).OfType<string>().ToList();
            if (definition != null || examples.Count > 0)
                senses.Add(new PhrasalSense(definition, examples));
        }
        return senses;
    }

    private static bool IsEntryPage(Uri uri) =>
        uri.AbsolutePath.StartsWith("/definition/english/", StringComparison.OrdinalIgnoreCase);

    private static string StripFragment(string url)
    {
        int hash = url.IndexOf('#');
        return hash >= 0 ? url[..hash] : url;
    }

    private static string WithoutQuery(Uri uri) => uri.GetLeftPart(UriPartial.Path);
}
