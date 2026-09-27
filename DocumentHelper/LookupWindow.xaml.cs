using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using System.Windows.Threading;
using DocumentHelper.Lookup;
using DocumentHelper.Native;

namespace DocumentHelper;

/// <summary>
/// Borderless popup shown next to the mouse cursor. It shows the Turkish meanings straight away; the book
/// icon (visible when anything was found) switches to phrasal verbs and example sentences.
/// </summary>
public partial class LookupWindow : Window
{
    private const int MaxQueryLength = 60;
    private const int WordExampleCount = 6;
    private const int MaxOxfordPhrasalVerbs = 30;
    private const int MaxTurengOnlyPhrasalVerbs = 10;
    private static readonly Duration FadeInDuration = TimeSpan.FromMilliseconds(160);

    private readonly IReadOnlyList<ITranslationSource> _sources;
    private readonly PhrasalVerbService _phrasalVerbService;
    private readonly TatoebaExamples _examples;
    private readonly ObservableCollection<SourceViewModel> _results = [];
    private readonly ObservableCollection<PhrasalVerbViewModel> _phrasalVerbs = [];
    private readonly ObservableCollection<ExampleSentence> _wordExamples = [];
    private CancellationTokenSource? _lookupCts;
    private NativeMethods.POINT _anchor;
    private bool _openUpwards;
    private bool _anchored; // False only while pre-warming off-screen.
    private int _oxfordCount;
    private int _turengOnlyCount;

    public LookupWindow(IReadOnlyList<ITranslationSource> sources, PhrasalVerbService phrasalVerbService,
        TatoebaExamples examples, string hotkeyText)
    {
        InitializeComponent();
        _sources = sources;
        _phrasalVerbService = phrasalVerbService;
        _examples = examples;
        SourcesList.ItemsSource = _results;
        PhrasalList.ItemsSource = _phrasalVerbs;
        ExampleList.ItemsSource = _wordExamples;
        FooterText.Text = $"Select a word anywhere and press {hotkeyText}  ·  Esc to close";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle).AddHook(WndProc);
    }

    /// <summary>
    /// Results arrive over time and change the popup's height. Windows asks before every move/resize; answering
    /// with the anchored position here makes resize and move one step, so the popup is never drawn anywhere
    /// else (moving it after the resize shows it in the wrong place for a frame, or even off-screen).
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_WINDOWPOSCHANGING && _anchored)
        {
            var pos = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.WINDOWPOS>(lParam);
            int width = pos.cx, height = pos.cy;
            if ((pos.flags & NativeMethods.SWP_NOSIZE) != 0)
            {
                if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return IntPtr.Zero;
                width = rect.Right - rect.Left;
                height = rect.Bottom - rect.Top;
            }
            if (TryGetAnchoredPosition(width, height, out int x, out int y))
            {
                pos.x = x;
                pos.y = y;
                pos.flags &= ~NativeMethods.SWP_NOMOVE;
                System.Runtime.InteropServices.Marshal.StructureToPtr(pos, lParam, false);
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Builds and renders the window once, invisibly, at start-up. The first Show() of a WPF window is slow
    /// (templates, fonts, effects); doing it early makes the first F10 as quick as later ones.
    /// </summary>
    public void Prewarm()
    {
        _anchored = false;
        ShowActivated = false;
        Opacity = 0;
        Left = Top = -10000;
        Show();
        UpdateLayout();
        Hide();
        ShowActivated = true;
    }

    /// <summary>Shows the popup at the mouse cursor and looks up <paramref name="selectedText"/> (if any).</summary>
    public void ShowAtCursor(string? selectedText)
    {
        NativeMethods.GetCursorPos(out _anchor);

        string? word = CleanUp(selectedText);
        if (word == null)
        {
            CancelLookup();
            ResetView();
            SearchBox.Text = "";
            ShowInfo(string.IsNullOrWhiteSpace(selectedText)
                ? "No selected text was found. Type a word above and press Enter."
                : $"The selection is too long. Select a single word or a short phrase (max {MaxQueryLength} characters).");
        }
        else
        {
            StartLookup(word);
        }

        AppearAtAnchor();
        // Keyboard focus goes to the popup itself, not the search box: a focused text box makes writing
        // assistants (e.g. Grammarly) pop up their own animated badge next to the popup. Typing still works,
        // see OnPreviewTextInput.
        RootPanel.Focus();
    }

    /// <summary>Typing while the search box is not focused starts a new search, replacing the old word.</summary>
    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (SearchBox.IsKeyboardFocusWithin || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        SearchBox.Focus();
        SearchBox.Text = e.Text;
        SearchBox.CaretIndex = SearchBox.Text.Length;
        e.Handled = true;
    }

    /// <summary>
    /// Shows the window fully transparent, moves it next to the cursor and only then fades it in, so it never
    /// flashes at its previous or default position.
    /// </summary>
    private void AppearAtAnchor()
    {
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        // Decide the side before showing: from now on every resize is positioned by WndProc.
        _openUpwards = ShouldOpenUpwards();
        _anchored = true;
        if (!IsVisible) Show();
        UpdateLayout();
        PlaceNearAnchor();
        NativeMethods.ForceForeground(new WindowInteropHelper(this).Handle);
        Activate();

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, FadeInDuration) { EasingFunction = ease });
        SlideTransform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(_openUpwards ? 6 : -6, 0, FadeInDuration) { EasingFunction = ease });
    }

    private void StartLookup(string word)
    {
        CancelLookup();
        var cts = _lookupCts = new CancellationTokenSource();

        ResetView();
        ShowInfo(null);
        SearchBox.Text = word;
        ExamplesHeader.Text = $"Example sentences with “{word}”";
        TatoebaLink.NavigateUri = new Uri(TatoebaExamples.GetPageUrl(word));

        var pending = new List<(ITranslationSource Source, SourceViewModel Vm)>();
        foreach (var source in _sources)
        {
            var vm = new SourceViewModel(source.Name, source.GetPageUrl(word));
            _results.Add(vm);
            pending.Add((source, vm));
        }

        // Start the network requests after the popup has drawn its first frame: setting them up takes a
        // noticeable moment the first time, and the popup should appear instantly.
        var token = cts.Token; // Captured now: a newer lookup disposes this source.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (token.IsCancellationRequested) return;
            foreach (var (source, vm) in pending)
                _ = RunSourceAsync(source, vm, word, token);
            _ = LoadOxfordPhrasalVerbsAsync(word, token);
            _ = LoadWordExamplesAsync(word, token);
        });
    }

    private void ResetView()
    {
        _results.Clear();
        _phrasalVerbs.Clear();
        _wordExamples.Clear();
        _oxfordCount = 0;
        _turengOnlyCount = 0;
        ExtrasButton.IsChecked = false;
        UpdateExtras();
    }

    private async Task RunSourceAsync(ITranslationSource source, SourceViewModel vm, string word, CancellationToken ct)
    {
        try
        {
            var result = await source.LookupAsync(word, ct);
            if (ct.IsCancellationRequested) return;
            vm.Apply(result);
            foreach (var verb in result.PhrasalVerbs)
                AddPhrasalVerb(verb.Phrase, oxfordUrl: null, turengTurkish: verb.Turkish, ct);
            UpdateExtras();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A newer lookup replaced this one.
        }
        catch (Exception ex)
        {
            vm.Message = "Could not get results: " + ex.Message;
            App.Log($"{source.Name} lookup for '{word}' failed: {ex}");
        }
        finally
        {
            vm.IsLoading = false;
        }
    }

    private async Task LoadOxfordPhrasalVerbsAsync(string word, CancellationToken ct)
    {
        var links = await _phrasalVerbService.GetOxfordListAsync(word);
        if (ct.IsCancellationRequested) return;
        foreach (var link in links)
            AddPhrasalVerb(link.Phrase, link.Url, turengTurkish: null, ct);
        UpdateExtras();
    }

    private async Task LoadWordExamplesAsync(string word, CancellationToken ct)
    {
        try
        {
            var examples = await _examples.SearchAsync(word, WordExampleCount);
            if (ct.IsCancellationRequested) return;
            foreach (var example in examples) _wordExamples.Add(example);
            UpdateExtras();
        }
        catch (Exception ex)
        {
            App.Log("Example sentences failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Merges phrasal verbs from Oxford and Tureng. Oxford's (a curated learner's list) come first; Tureng adds
    /// ones Oxford lacks and a Turkish fallback meaning. "look sth up" and "look up" are the same entry.
    /// </summary>
    private void AddPhrasalVerb(string phrase, string? oxfordUrl, string? turengTurkish, CancellationToken ct)
    {
        string key = PhrasalText.Normalize(phrase);
        var existing = _phrasalVerbs.FirstOrDefault(p => PhrasalText.Normalize(p.Phrase) == key);
        if (existing != null)
        {
            if (existing.OxfordUrl == null && oxfordUrl != null)
            {
                // Tureng's list arrived first; now that Oxford confirms it, move it into the Oxford block.
                existing.OxfordUrl = oxfordUrl;
                _turengOnlyCount--;
                _phrasalVerbs.Move(_phrasalVerbs.IndexOf(existing), _oxfordCount++);
            }
            existing.TurengTurkish ??= turengTurkish;
            return;
        }

        var vm = new PhrasalVerbViewModel(phrase) { OxfordUrl = oxfordUrl, TurengTurkish = turengTurkish };
        if (oxfordUrl != null)
        {
            if (_oxfordCount >= MaxOxfordPhrasalVerbs) return;
            _phrasalVerbs.Insert(_oxfordCount++, vm);
        }
        else
        {
            if (_turengOnlyCount >= MaxTurengOnlyPhrasalVerbs) return;
            _turengOnlyCount++;
            _phrasalVerbs.Add(vm);
        }

        if (ExtrasButton.IsChecked == true) _ = LoadPhrasalDetailsAsync(vm, ct);
    }

    /// <summary>Details per phrasal verb are only fetched once the user opens the panel.</summary>
    private async Task LoadPhrasalDetailsAsync(PhrasalVerbViewModel vm, CancellationToken ct)
    {
        if (vm.DetailsRequested) return;
        vm.DetailsRequested = true;
        vm.IsLoading = true;
        try
        {
            // Give Tureng a moment to supply its fallback meaning when Oxford's list arrived first.
            if (vm.TurengTurkish == null && _results.Any(r => r.IsLoading))
                await Task.Delay(300, ct);
            var details = await _phrasalVerbService.GetDetailsAsync(vm.Phrase, vm.OxfordUrl, vm.TurengTurkish);
            if (!ct.IsCancellationRequested) vm.Details = details;
        }
        catch (OperationCanceledException)
        {
            // A newer lookup replaced this one.
        }
        catch (Exception ex)
        {
            App.Log($"Details for '{vm.Phrase}' failed: {ex}");
        }
        finally
        {
            vm.IsLoading = false;
        }
    }

    /// <summary>The book icon only appears when there are phrasal verbs or example sentences to show.</summary>
    private void UpdateExtras()
    {
        bool any = _phrasalVerbs.Count > 0 || _wordExamples.Count > 0;
        ExtrasButton.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        ExtrasButton.ToolTip = _phrasalVerbs.Count > 0
            ? $"{_phrasalVerbs.Count} phrasal verb(s) and example sentences"
            : "Example sentences";
        PhrasalSection.Visibility = _phrasalVerbs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ExamplesSection.Visibility = _wordExamples.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        bool showExtras = any && ExtrasButton.IsChecked == true;
        ExtrasPanel.Visibility = showExtras ? Visibility.Visible : Visibility.Collapsed;
        SourcesList.Visibility = showExtras ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnExtrasToggled(object sender, RoutedEventArgs e)
    {
        UpdateExtras();
        Scroller.ScrollToTop();
        if (ExtrasButton.IsChecked == true && _lookupCts != null)
        {
            foreach (var vm in _phrasalVerbs)
                _ = LoadPhrasalDetailsAsync(vm, _lookupCts.Token);
        }
    }

    private void CancelLookup()
    {
        _lookupCts?.Cancel();
        _lookupCts?.Dispose();
        _lookupCts = null;
    }

    /// <summary>Trims whitespace and surrounding punctuation/quotes; returns null if nothing usable is left.</summary>
    internal static string? CleanUp(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string cleaned = Regex.Replace(text, @"\s+", " ").Trim();
        cleaned = cleaned.Trim('"', '\'', '“', '”', '‘', '’', '«', '»', '„', '(', ')', '[', ']', '{', '}',
                               '.', ',', ';', ':', '!', '?', '¿', '¡', '…', '-', '–', '—', ' ');
        if (cleaned.Length == 0 || cleaned.Length > MaxQueryLength) return null;
        return cleaned;
    }

    private void ShowInfo(string? text)
    {
        InfoText.Text = text;
        InfoText.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Decided once per appearance: below the cursor if the popup fits there at its maximum height, otherwise
    /// above. Deciding only once means growing results never make it jump from one side to the other.
    /// </summary>
    private bool ShouldOpenUpwards()
    {
        if (!TryGetWorkArea(out var work)) return false;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
        int maxHeight = (int)(MaxHeight * scale);
        int spaceBelow = work.Bottom - (_anchor.Y + 12);
        int spaceAbove = _anchor.Y - 4 - work.Top;
        return spaceBelow < maxHeight && spaceAbove > spaceBelow;
    }

    /// <summary>Moves the popup to the anchor; the actual position is computed in <see cref="WndProc"/>.</summary>
    private void PlaceNearAnchor()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>
    /// Below-right of the cursor, or above it (see <see cref="ShouldOpenUpwards"/>) with the bottom edge fixed
    /// so the popup grows upwards; clamped to the monitor's work area. Physical pixels, so it is correct on
    /// mixed-DPI multi-monitor setups.
    /// </summary>
    private bool TryGetAnchoredPosition(int width, int height, out int x, out int y)
    {
        x = y = 0;
        if (!TryGetWorkArea(out var work)) return false;
        x = _anchor.X + 4;
        y = _openUpwards ? _anchor.Y - 4 - height : _anchor.Y + 12;
        x = Math.Max(work.Left, Math.Min(x, work.Right - width));
        y = Math.Max(work.Top, Math.Min(y, work.Bottom - height));
        return true;
    }

    private bool TryGetWorkArea(out NativeMethods.RECT work)
    {
        var monitor = NativeMethods.MonitorFromPoint(_anchor, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        bool ok = NativeMethods.GetMonitorInfo(monitor, ref info);
        work = info.rcWork;
        return ok;
    }

    private void OnSearchBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        string? word = CleanUp(SearchBox.Text);
        if (word == null) return;
        StartLookup(word);
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Close when the user moves to another application. Focus moving to one of our own windows (the
    /// background browser can grab it while loading a page) must not close the popup; take focus back instead.
    /// </summary>
    private async void OnDeactivated(object? sender, EventArgs e)
    {
        await Task.Delay(150); // Let the focus change settle before looking at the new foreground window.
        if (!IsVisible || IsActive) return;

        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero) return; // Focus is in transition (or the screen got locked).

        if (IsOwnWindow(foreground))
            NativeMethods.ForceForeground(new WindowInteropHelper(this).Handle);
        else
            Hide();
    }

    /// <summary>Our own windows, including the WebView2 browser processes that render the hidden Tureng page.</summary>
    private static bool IsOwnWindow(IntPtr hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId);
        if (processId == (uint)Environment.ProcessId) return true;
        try
        {
            return Process.GetProcessById((int)processId).ProcessName.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false; // The process has already exited.
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Hide();

    private void OnOpenLink(object sender, RequestNavigateEventArgs e)
    {
        if (e.Uri == null) return;
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}

/// <summary>Collapses an element when the bound value is null or an empty string.</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null || value is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
