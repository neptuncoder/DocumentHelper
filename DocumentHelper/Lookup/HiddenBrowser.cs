using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using DocumentHelper.Native;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DocumentHelper.Lookup;

/// <summary>
/// A real Edge (WebView2) browser kept in an off-screen window. Used for sites protected by
/// Cloudflare's JavaScript challenge, which blocks plain HTTP requests. Cookies are persisted, so the
/// challenge normally has to be passed only occasionally. If the site asks for a manual check
/// ("I am human"), the window is brought on-screen until the page becomes readable.
/// </summary>
internal sealed class HiddenBrowser
{
    private static readonly TimeSpan AutoTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ManualTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ShowChallengeAfter = TimeSpan.FromSeconds(6);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _title;
    private Window? _window;
    private WebView2? _webView;
    private Task? _initTask;
    private ulong _currentNavigationId;
    private volatile bool _documentReady;
    private bool _awaitingNavigationStart;
    private string? _navigationError;
    private bool _allowClose;

    public HiddenBrowser(string title) => _title = title;

    /// <summary>True while the window is on-screen so the user can pass a bot check.</summary>
    public bool IsShownForUser { get; private set; }

    /// <summary>
    /// Creates the browser and opens <paramref name="url"/> in the background, so the first real lookup
    /// does not pay for browser start-up and the site's bot check.
    /// </summary>
    public async Task WarmUpAsync(string url)
    {
        await _gate.WaitAsync();
        try
        {
            await EnsureInitializedAsync();
            _webView!.CoreWebView2.Navigate(url);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Navigates to <paramref name="url"/> and repeatedly runs <paramref name="extractScript"/> until it
    /// returns JSON whose "state" is "ready". The script must return "challenge" while a bot check is shown.
    /// </summary>
    public async Task<JsonElement> LoadAndExtractAsync(string url, string extractScript, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureInitializedAsync();
            var core = _webView!.CoreWebView2;

            _navigationError = null;
            _documentReady = false;
            _awaitingNavigationStart = true; // Ignore events that still belong to the previous page.
            core.Navigate(url);

            var started = DateTime.UtcNow;
            while (true)
            {
                await Task.Delay(100, ct);
                var elapsed = DateTime.UtcNow - started;

                if (elapsed > (IsShownForUser ? ManualTimeout : AutoTimeout))
                    throw new TimeoutException("The page took too long to load.");
                if (_navigationError != null)
                    throw new InvalidOperationException(_navigationError);
                // Read as soon as the HTML is parsed; waiting for the full load (ads, images) is much slower.
                if (!_documentReady)
                    continue;

                string raw = await core.ExecuteScriptAsync(extractScript);
                // ExecuteScriptAsync JSON-encodes the returned value, and our script returns a JSON string.
                string? json = JsonSerializer.Deserialize<string>(raw);
                if (json == null) continue;

                var result = JsonDocument.Parse(json).RootElement;
                string? state = result.GetProperty("state").GetString();

                if (state == "ready")
                    return result;

                if (state == "challenge" && !IsShownForUser && elapsed > ShowChallengeAfter)
                    ShowForUser();
            }
        }
        finally
        {
            if (IsShownForUser) HideFromUser();
            _gate.Release();
        }
    }

    public void Close()
    {
        _allowClose = true;
        _window?.Close();
    }

    private Task EnsureInitializedAsync() => _initTask ??= InitializeAsync();

    private async Task InitializeAsync()
    {
        _webView = new WebView2();
        _window = new Window
        {
            Title = _title,
            Width = 900,
            Height = 700,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStyle = WindowStyle.ToolWindow,
            Content = _webView,
        };
        _window.SourceInitialized += (_, _) =>
        {
            // WebView2 grabs keyboard focus when it starts and navigates, which would deactivate (and close)
            // the lookup popup. A no-activate window never becomes the foreground window; the page can still
            // be clicked, which is all a "verify you are human" check needs.
            var hwnd = new WindowInteropHelper(_window).Handle;
            long style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE,
                new IntPtr(style | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW));
        };
        _window.Closing += (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true; // The user closed the verification window: just hide it again.
            HideFromUser();
        };
        HideFromUser();
        // The window must be "shown" for WebView2 to create its HWND and run scripts at full speed;
        // it simply lives outside the visible desktop.
        _window.Show();

        string userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DocumentHelper", "WebView2");
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
        await _webView.EnsureCoreWebView2Async(environment);

        var core = _webView.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.NavigationStarting += (_, e) =>
        {
            _currentNavigationId = e.NavigationId;
            _awaitingNavigationStart = false;
            _documentReady = false;
        };
        core.DOMContentLoaded += (_, e) =>
        {
            if (!_awaitingNavigationStart && e.NavigationId == _currentNavigationId) _documentReady = true;
        };
        core.NavigationCompleted += (_, e) =>
        {
            if (_awaitingNavigationStart || e.NavigationId != _currentNavigationId) return;
            // HTTP errors (e.g. Cloudflare's 403 challenge page) still render a page we can inspect;
            // only real network failures (no HTTP status at all) are errors.
            if (!e.IsSuccess && e.HttpStatusCode == 0 && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                _navigationError = $"Could not reach the site ({e.WebErrorStatus}).";
            _documentReady = true;
        };
        // Pop-ups/ads opening new windows are not wanted.
        core.NewWindowRequested += (_, e) => e.Handled = true;

        // Images, fonts and media are not needed to read the text; skipping them makes pages load faster.
        // Cloudflare's own resources are left alone so a manual check still renders correctly.
        foreach (var context in new[] { CoreWebView2WebResourceContext.Image, CoreWebView2WebResourceContext.Font, CoreWebView2WebResourceContext.Media })
            core.AddWebResourceRequestedFilter("*", context);
        core.WebResourceRequested += (_, e) =>
        {
            if (!e.Request.Uri.Contains("cloudflare", StringComparison.OrdinalIgnoreCase))
                e.Response = environment.CreateWebResourceResponse(null, 204, "No Content", "");
        };
    }

    private void ShowForUser()
    {
        if (_window == null) return;
        IsShownForUser = true;
        var area = SystemParameters.WorkArea;
        _window.Title = _title + " – please complete the check; this window hides automatically";
        _window.Left = area.Left + (area.Width - _window.Width) / 2;
        _window.Top = area.Top + (area.Height - _window.Height) / 2;
        _window.Topmost = true;
    }

    private void HideFromUser()
    {
        IsShownForUser = false;
        if (_window == null) return;
        _window.Topmost = false;
        _window.Title = _title;
        _window.Left = -32000;
        _window.Top = -32000;
    }
}
