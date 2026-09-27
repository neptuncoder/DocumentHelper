using System.Text.Json;

namespace DocumentHelper.Lookup;

/// <summary>
/// Tureng (tureng.com), Turkish–English dictionary. Tureng has no other language paired with Turkish.
/// The site is behind Cloudflare's JavaScript challenge, so it is read through <see cref="HiddenBrowser"/>.
/// </summary>
public sealed class TurengSource : ITranslationSource
{
    private const string HomeUrl = "https://tureng.com/en/turkish-english";
    private const int MaxMeanings = 25;
    private const int MaxWordsPerMeaning = 12;
    private const int MaxPhrasalVerbs = 15;
    private const int MaxWordsPerPhrasalVerb = 6;

    private static readonly HashSet<string> Particles = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "across", "after", "against", "ahead", "along", "apart", "around", "aside", "at", "away", "back",
        "behind", "by", "down", "for", "forward", "in", "into", "of", "off", "on", "onto", "out", "over", "past",
        "round", "through", "to", "together", "under", "up", "upon", "with", "without",
    };

    // Object placeholders Tureng uses inside phrasal verbs ("look sth up").
    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "sb", "sth", "sb's", "someone", "somebody", "something", "someone's", "oneself", "one's", "it", "smb", "smth",
    };

    // Table 0 is "Meanings of X in Turkish English Dictionary"; table 1 lists terms containing X, which is
    // where phrasal verbs ("look after", "look into") are found.
    private const string ExtractScript = """
        (() => {
          const title = document.title || '';
          if (/just a moment|bir dakika|attention required|security check/i.test(title) ||
              document.querySelector('#challenge-form, #challenge-running, #cf-challenge-running, .cf-turnstile'))
            return JSON.stringify({ state: 'challenge' });
          const tables = [...document.querySelectorAll('table.searchResultsTable')];
          if (tables.length === 0 && document.readyState === 'loading')
            return JSON.stringify({ state: 'loading' });
          const rows = [];
          tables.forEach((table, index) => {
            for (const tr of table.querySelectorAll('tr')) {
              const tds = [...tr.querySelectorAll('td')];
              const trCell = tds.find(td => td.getAttribute('lang') === 'tr');
              const srcCell = tds.find(td => td.hasAttribute('lang') && td.getAttribute('lang') !== 'tr');
              if (!trCell || !srcCell) continue;
              const catCell = tds.find(td => !td.hasAttribute('lang') && /[^\s\d]/.test(td.textContent));
              const link = srcCell.querySelector('a');
              const pos = srcCell.querySelector('i');
              rows.push({
                table: index,
                term: (link || srcCell).textContent.trim(),
                pos: pos ? pos.textContent.trim() : '',
                category: catCell ? catCell.textContent.trim() : '',
                turkish: (trCell.querySelector('a') || trCell).textContent.trim()
              });
            }
          });
          const suggestions = [...document.querySelectorAll('.suggestion-list a')]
            .map(a => a.textContent.trim()).filter(s => s).slice(0, 8);
          return JSON.stringify({ state: 'ready', rows, suggestions });
        })()
        """;

    private readonly HiddenBrowser _browser = new("Tureng");

    public string Name => "Tureng";

    public string GetPageUrl(string word) => HomeUrl + "/" + Uri.EscapeDataString(word);

    /// <summary>Starts the browser and passes Tureng's bot check before the first lookup.</summary>
    public Task WarmUpAsync() => _browser.WarmUpAsync(HomeUrl);

    public async Task<SourceResult> LookupAsync(string word, CancellationToken cancellationToken)
    {
        string url = GetPageUrl(word);
        var page = await _browser.LoadAndExtractAsync(url, ExtractScript, cancellationToken);
        var rows = ReadRows(page.GetProperty("rows"));

        var meanings = GroupMeanings(rows.Where(r => r.Table == 0));
        var phrasalVerbs = FindPhrasalVerbs(rows, word);
        if (meanings.Count > 0)
            return new SourceResult(Name, url, meanings, null) { PhrasalVerbs = phrasalVerbs };

        var suggestions = page.GetProperty("suggestions").EnumerateArray().Select(s => s.GetString()!).ToList();
        string message = suggestions.Count > 0
            ? "No exact match. Did you mean: " + string.Join(", ", suggestions) + "?"
            : "No results (Tureng covers English and Turkish words).";
        return SourceResult.Empty(Name, url, message) with { PhrasalVerbs = phrasalVerbs };
    }

    public void Close() => _browser.Close();

    private sealed record Row(int Table, string Term, string Pos, string Category, string Turkish);

    private static List<Row> ReadRows(JsonElement rows) => rows.EnumerateArray()
        .Select(r => new Row(
            r.GetProperty("table").GetInt32(),
            r.GetProperty("term").GetString() ?? "",
            r.GetProperty("pos").GetString() ?? "",
            r.GetProperty("category").GetString() ?? "",
            r.GetProperty("turkish").GetString() ?? ""))
        .Where(r => r.Term.Length > 0 && r.Turkish.Length > 0)
        .ToList();

    /// <summary>
    /// Tureng lists one Turkish word per row; merge rows with the same source term, part of speech and
    /// category into one meaning ("ev, konut, hane"), keeping Tureng's order (most common first).
    /// </summary>
    private static List<Meaning> GroupMeanings(IEnumerable<Row> rows) =>
        rows.GroupBy(r => (r.Term, r.Pos, r.Category))
            .Take(MaxMeanings)
            .Select(g =>
            {
                string head = g.Key.Pos.Length > 0 ? $"{g.Key.Term} ({g.Key.Pos})" : g.Key.Term;
                string context = g.Key.Category.Length > 0 ? $"{head} · {g.Key.Category}" : head;
                return new Meaning(JoinWords(g.Select(r => r.Turkish), MaxWordsPerMeaning), context, null);
            })
            .ToList();

    /// <summary>
    /// Phrasal verbs are "headword + particle(s)", optionally with an object placeholder
    /// ("look after", "look sth up"), or anything Tureng itself files under "Phrasals".
    /// </summary>
    private static List<PhrasalVerb> FindPhrasalVerbs(List<Row> rows, string query)
    {
        // The headword may differ from the query ("looked" → "look"); Tureng's first row shows it.
        var headwords = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { query };
        var first = rows.FirstOrDefault(r => r.Table == 0);
        if (first != null) headwords.Add(first.Term);

        return rows
            .Where(r => !headwords.Contains(r.Term) && IsPhrasalVerb(r, headwords))
            .GroupBy(r => r.Term, StringComparer.OrdinalIgnoreCase)
            .Take(MaxPhrasalVerbs)
            .Select(g => new PhrasalVerb(g.Key, JoinWords(g.Select(r => r.Turkish), MaxWordsPerPhrasalVerb)))
            .ToList();
    }

    private static bool IsPhrasalVerb(Row row, HashSet<string> headwords)
    {
        string[] words = row.Term.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2 || !words.Any(headwords.Contains)) return false;
        if (row.Category.Equals("Phrasals", StringComparison.OrdinalIgnoreCase)) return true;

        return row.Pos == "v."
               && headwords.Contains(words[0])
               && words.Skip(1).All(w => Particles.Contains(w) || Placeholders.Contains(w))
               && words.Skip(1).Any(Particles.Contains);
    }

    private static string JoinWords(IEnumerable<string> words, int max)
    {
        var distinct = words.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string text = string.Join(", ", distinct.Take(max));
        return distinct.Count > max ? text + $" … (+{distinct.Count - max})" : text;
    }
}
