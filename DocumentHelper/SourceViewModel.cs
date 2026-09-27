using System.ComponentModel;
using System.Runtime.CompilerServices;
using DocumentHelper.Lookup;

namespace DocumentHelper;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }

    protected void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>State of one dictionary source inside the popup (loading → meanings or a message).</summary>
public sealed class SourceViewModel : ViewModelBase
{
    private bool _isLoading = true;
    private string? _message;
    private string? _url;
    private IReadOnlyList<Meaning> _meanings = [];

    public SourceViewModel(string name, string? url)
    {
        Name = name;
        _url = url;
    }

    public string Name { get; }

    public string? Url { get => _url; private set => Set(ref _url, value); }

    public bool IsLoading { get => _isLoading; set => Set(ref _isLoading, value); }

    public string? Message { get => _message; set => Set(ref _message, value); }

    public IReadOnlyList<Meaning> Meanings { get => _meanings; private set => Set(ref _meanings, value); }

    public void Apply(SourceResult result)
    {
        Url = result.Url ?? Url;
        Meanings = result.Meanings;
        Message = result.Message;
        IsLoading = false;
    }
}

/// <summary>
/// A phrasal verb in the popup. The list comes from Oxford and Tureng; the Turkish meaning, definitions and
/// examples are loaded when the panel is opened (see <see cref="PhrasalVerbService"/>).
/// </summary>
public sealed class PhrasalVerbViewModel : ViewModelBase
{
    private bool _isLoading;
    private PhrasalVerbDetails? _details;

    public PhrasalVerbViewModel(string phrase) => Phrase = phrase;

    public string Phrase { get; }

    /// <summary>Oxford entry link, if the phrasal verb is in Oxford's list for the word.</summary>
    public string? OxfordUrl { get; set; }

    /// <summary>Tureng's Turkish meaning, used when Cambridge has none.</summary>
    public string? TurengTurkish { get; set; }

    public bool DetailsRequested { get; set; }

    public bool IsLoading { get => _isLoading; set => Set(ref _isLoading, value); }

    public PhrasalVerbDetails? Details
    {
        get => _details;
        set
        {
            Set(ref _details, value);
            foreach (var name in new[] { nameof(Turkish), nameof(Separator), nameof(Pattern), nameof(SourceLabel), nameof(Senses),
                                         nameof(TranslatedExamples), nameof(MoreUrl), nameof(NothingFound) })
                OnPropertyChanged(name);
        }
    }

    public string? Turkish => Details?.Turkish;

    public string? Separator => Turkish != null ? "  –  " : null;

    /// <summary>Usage pattern such as "look after somebody/something".</summary>
    public string? Pattern => Details?.Pattern;

    /// <summary>E.g. "Turkish: Cambridge · English: Oxford".</summary>
    public string? SourceLabel
    {
        get
        {
            if (Details == null) return null;
            var parts = new List<string>();
            if (Details.TurkishSource != null) parts.Add("Turkish: " + Details.TurkishSource);
            if (Details.SensesSource != null) parts.Add("English: " + Details.SensesSource);
            else if (Details.TranslatedExamples.Count > 0) parts.Add("Examples: Tatoeba");
            return parts.Count > 0 ? string.Join(" · ", parts) : null;
        }
    }

    public IReadOnlyList<PhrasalSense> Senses => Details?.Senses ?? [];

    public IReadOnlyList<ExampleSentence> TranslatedExamples => Details?.TranslatedExamples ?? [];

    public Uri? MoreUrl => Details?.MoreUrl is { } url ? new Uri(url) : OxfordUrl != null ? new Uri(OxfordUrl) : null;

    public string? NothingFound =>
        Details != null && Turkish == null && Senses.Count == 0 && TranslatedExamples.Count == 0
            ? "No details found in Cambridge, Oxford or Tatoeba."
            : null;
}
