using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows;
using DocumentHelper.Lookup;
using DocumentHelper.Native;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace DocumentHelper;

/// <summary>
/// Tray application: registers the global hotkey, reads the selected text from the active app
/// and shows the Turkish meanings in a popup next to the cursor.
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstance;
    private AppSettings _settings = new();
    private HotkeyManager? _hotkey;
    private Forms.NotifyIcon? _trayIcon;
    private LookupWindow? _popup;
    private TurengSource? _tureng;
    private readonly CambridgeSource _cambridge = new();
    private readonly TatoebaExamples _examples = new();
    private PhrasalVerbService? _phrasalVerbs;
    private bool _busy;

    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DocumentHelper", "error.log");

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Logging must never take the app down.
        }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A tray app should survive unexpected errors; record them instead of crashing silently.
        DispatcherUnhandledException += (_, args) =>
        {
            Log("Unhandled: " + args.Exception);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log("Unobserved task: " + args.Exception);
            args.SetObserved();
        };

        _settings = AppSettings.Load();
        var sources = CreateSources();
        _phrasalVerbs = new PhrasalVerbService(_cambridge, new OxfordSource(), _examples);

        // Diagnostic mode: DocumentHelper.exe --test <word> <output file>
        if (e.Args.Length == 3 && e.Args[0] == "--test")
        {
            await RunTestAsync(sources, _phrasalVerbs, _examples, e.Args[1], e.Args[2]);
            Shutdown();
            return;
        }

        // DocumentHelper.exe --uninstall: remove the app without the tray menu (closes a running copy first).
        if (e.Args.Length == 1 && e.Args[0] == "--uninstall")
        {
            foreach (var other in Process.GetProcessesByName("DocumentHelper").Where(p => p.Id != Environment.ProcessId))
            {
                try { other.Kill(); other.WaitForExit(3000); } catch (Exception) { /* already gone */ }
            }
            Installer.Uninstall();
            Shutdown();
            return;
        }

        _singleInstance = new Mutex(true, @"Local\DocumentHelper.TurkishLookup", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("Document Helper is already running (see the system tray).", "Document Helper");
            Shutdown();
            return;
        }

        if (!_settings.FirstRunDone && AskToStartWithWindows())
            return; // Relaunched from the installed copy.

        string hotkeyText = _settings.Hotkey;
        _popup = new LookupWindow(sources, _phrasalVerbs, _examples, hotkeyText);
        _popup.Prewarm();
        _trayIcon = CreateTrayIcon(hotkeyText);

        // Start the background browser now, so the first lookup shows Tureng's result right away.
        if (_tureng != null)
            _ = _tureng.WarmUpAsync().ContinueWith(t => Log("Tureng warm-up failed: " + t.Exception),
                TaskContinuationOptions.OnlyOnFaulted);

        _hotkey = new HotkeyManager();
        _hotkey.Pressed += async (_, _) => await OnHotkeyAsync();
        if (!_settings.TryGetHotkey(out var modifiers, out var key) || !_hotkey.Register(modifiers, key))
        {
            MessageBox.Show(
                $"The hotkey \"{_settings.Hotkey}\" could not be registered (invalid, or already used by another program).\n\n" +
                $"Change \"Hotkey\" in:\n{AppSettings.FilePath}\nand restart. You can still use the tray icon.",
                "Document Helper", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else
        {
            _trayIcon.ShowBalloonTip(4000, "Document Helper is running",
                $"Select a word in any app and press {hotkeyText} to see its Turkish meaning.", Forms.ToolTipIcon.Info);
        }
    }

    /// <summary>
    /// First start on a PC: offer to start with Windows. For a downloaded copy this also installs it (see
    /// <see cref="Installer"/>) and restarts from the installed copy. Returns true if this instance is exiting.
    /// </summary>
    private bool AskToStartWithWindows()
    {
        _settings.FirstRunDone = true;
        _settings.Save();
        if (Installer.IsAutostartEnabled()) return false;

        var answer = MessageBox.Show(
            "Start Document Helper automatically when you sign in to Windows?\n\n" +
            $"It runs quietly in the system tray: select a word in any app and press {_settings.Hotkey} " +
            "to see its Turkish meaning.\n\nYou can change this later by right-clicking the red TR icon in the tray.",
            "Document Helper", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return false;

        string? startupExe;
        try
        {
            startupExe = Installer.SetAutostart(true);
        }
        catch (Exception ex)
        {
            Log("Installing failed: " + ex);
            MessageBox.Show("Could not set up automatic start: " + ex.Message, "Document Helper",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (startupExe == null || string.Equals(startupExe, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            return false;

        // Continue from the installed copy, so the downloaded file can be deleted.
        _singleInstance!.ReleaseMutex();
        _singleInstance.Dispose();
        _singleInstance = null;
        Process.Start(new ProcessStartInfo(startupExe) { UseShellExecute = true });
        Shutdown();
        return true;
    }

    /// <summary>Cambridge is shown first: its translations are sense-by-sense and edited for learners.</summary>
    private List<ITranslationSource> CreateSources()
    {
        var sources = new List<ITranslationSource>();
        if (_settings.UseCambridge)
            sources.Add(_cambridge);
        if (_settings.UseTureng)
        {
            _tureng = new TurengSource();
            sources.Add(_tureng);
        }
        return sources;
    }

    private async Task OnHotkeyAsync()
    {
        if (_busy) return; // Ignore key repeat while we are still copying the selection.
        _busy = true;
        try
        {
            string? text = null;
            try
            {
                text = await SelectionReader.GetSelectedTextAsync();
            }
            catch (Exception ex)
            {
                Log("Reading the selection failed: " + ex);
            }
            _popup!.ShowAtCursor(text);
        }
        finally
        {
            _busy = false;
        }
    }

    private Forms.NotifyIcon CreateTrayIcon(string hotkeyText)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Look up a word…", null, (_, _) => _popup!.ShowAtCursor(null));
        menu.Items.Add(new Forms.ToolStripMenuItem($"Hotkey: {hotkeyText}") { Enabled = false });
        menu.Items.Add(new Forms.ToolStripSeparator());

        var autostart = new Forms.ToolStripMenuItem("Start with Windows") { Checked = Installer.IsAutostartEnabled(), CheckOnClick = true };
        autostart.CheckedChanged += (_, _) =>
        {
            try
            {
                Installer.SetAutostart(autostart.Checked);
            }
            catch (Exception ex)
            {
                Log("Changing autostart failed: " + ex);
                MessageBox.Show("Could not change automatic start: " + ex.Message, "Document Helper");
            }
        };
        menu.Items.Add(autostart);
        menu.Items.Add("Edit settings (restart to apply)", null, (_, _) =>
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{AppSettings.FilePath}\"")));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Uninstall…", null, (_, _) => ConfirmUninstall());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());

        var icon = new Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = $"Document Helper – {hotkeyText} to translate the selection",
            ContextMenuStrip = menu,
            Visible = true,
        };
        icon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) _popup!.ShowAtCursor(null);
        };
        return icon;
    }

    /// <summary>The app icon (Assets\app.ico) at the tray's size for the current display scaling.</summary>
    private static Icon LoadTrayIcon()
    {
        using var stream = typeof(App).Assembly.GetManifestResourceStream("app.ico")!;
        return new Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    private void ConfirmUninstall()
    {
        var answer = MessageBox.Show(
            "Remove Document Helper from this PC?\n\nThis removes automatic start, the Start menu shortcut, " +
            "the installed copy and the app's settings.",
            "Uninstall Document Helper", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        Installer.Uninstall();
        Shutdown();
    }

    private static async Task RunTestAsync(IReadOnlyList<ITranslationSource> sources, PhrasalVerbService phrasalVerbs,
        TatoebaExamples examples, string word, string outputPath)
    {
        var sb = new StringBuilder();
        foreach (var source in sources)
        {
            sb.AppendLine($"== {source.Name}");
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var watch = Stopwatch.StartNew();
                var result = await source.LookupAsync(word, cts.Token);
                sb.AppendLine($"url: {result.Url}  ({watch.ElapsedMilliseconds} ms)");
                if (result.Message != null) sb.AppendLine($"message: {result.Message}");
                foreach (var m in result.Meanings)
                    sb.AppendLine($"- {m.Turkish}   [{m.Context}]   {m.Definition}");
                foreach (var p in result.PhrasalVerbs)
                    sb.AppendLine($"  PHRASAL {p.Phrase} = {p.Turkish}");
            }
            catch (Exception ex)
            {
                sb.AppendLine("ERROR: " + ex);
            }
        }
        sb.AppendLine("== Tatoeba");
        try
        {
            foreach (var s in await examples.SearchAsync(word, 6))
                sb.AppendLine($"- {s.English}  =>  {s.Turkish}");
        }
        catch (Exception ex)
        {
            sb.AppendLine("ERROR: " + ex);
        }

        sb.AppendLine("== Oxford phrasal verbs");
        var oxfordList = await phrasalVerbs.GetOxfordListAsync(word);
        sb.AppendLine(string.Join(", ", oxfordList.Select(l => l.Phrase)));
        foreach (var link in oxfordList.Take(4))
        {
            var d = await phrasalVerbs.GetDetailsAsync(link.Phrase, link.Url, turengTurkish: null);
            sb.AppendLine($"# {link.Phrase} = {d.Turkish} [Turkish: {d.TurkishSource}; English: {d.SensesSource}] {d.MoreUrl}");
            if (d.Pattern != null) sb.AppendLine($"    pattern: {d.Pattern}");
            foreach (var sense in d.Senses)
            {
                sb.AppendLine($"    def: {sense.Definition}");
                foreach (var x in sense.Examples) sb.AppendLine($"      • {x}");
            }
            foreach (var t in d.TranslatedExamples) sb.AppendLine($"      ~ {t.English} => {t.Turkish}");
        }
        File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkey?.Dispose();
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        _tureng?.Close();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
