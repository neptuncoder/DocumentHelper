using System.Runtime.InteropServices;
using System.Windows;
using static DocumentHelper.Native.NativeMethods;

namespace DocumentHelper.Native;

/// <summary>
/// Reads the text currently selected in the foreground application by sending Ctrl+C,
/// then puts the user's previous clipboard content back.
/// </summary>
internal static class SelectionReader
{
    public static async Task<string?> GetSelectedTextAsync()
    {
        // The user is most likely still holding the hotkey modifiers. If Shift/Alt/Win are down while
        // we send Ctrl+C, the target app sees e.g. Ctrl+Shift+C (DevTools in browsers), so wait for release.
        await WaitForModifiersReleasedAsync(TimeSpan.FromMilliseconds(1500));

        // A quick second press: put the user's clipboard back first, so it is what gets backed up again.
        RestorePending();
        var backup = TryBackupClipboard();
        uint sequenceBefore = GetClipboardSequenceNumber();

        SendCtrlC();

        // Wait for the target application to put the selection on the clipboard. Apps respond within tens
        // of milliseconds; the timeout only matters when nothing is selected, so keep it short.
        var deadline = DateTime.UtcNow.AddMilliseconds(350);
        while (GetClipboardSequenceNumber() == sequenceBefore && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        if (GetClipboardSequenceNumber() == sequenceBefore)
            return null; // Nothing was copied: no selection, or the app ignores Ctrl+C.

        // Some apps update the clipboard in several steps; give them a moment.
        await Task.Delay(30);
        string? text = Retry(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);

        // Put the user's clipboard back once the popup is on screen: restoring is comparatively slow and
        // must not delay the popup or stutter its fade-in.
        if (backup != null)
        {
            _pendingRestore = backup;
            _ = Task.Delay(RestoreDelay).ContinueWith(_ => { if (_pendingRestore == backup) RestorePending(); },
                TaskScheduler.FromCurrentSynchronizationContext());
        }

        return text;
    }

    private static readonly TimeSpan RestoreDelay = TimeSpan.FromMilliseconds(400);
    private static DataObject? _pendingRestore;

    private static void RestorePending()
    {
        var backup = _pendingRestore;
        _pendingRestore = null;
        if (backup != null)
            Retry(() => { Clipboard.SetDataObject(backup, copy: true); return true; });
    }

    /// <summary>
    /// The formats worth preserving. Reading every format an app offers can be slow (many are rendered on
    /// demand, e.g. by Visual Studio or Office) and some cannot be copied at all.
    /// </summary>
    private static readonly string[] BackupFormats =
    [
        DataFormats.UnicodeText, DataFormats.Text, DataFormats.Rtf, DataFormats.Html,
        DataFormats.Bitmap, DataFormats.FileDrop, "PNG",
    ];

    private static async Task WaitForModifiersReleasedAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && AnyModifierDown())
            await Task.Delay(15);
    }

    private static bool AnyModifierDown() =>
        IsDown(VK_SHIFT) || IsDown(VK_MENU) || IsDown(VK_LWIN) || IsDown(VK_RWIN) || IsDown(VK_CONTROL);

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static void SendCtrlC()
    {
        var inputs = new[]
        {
            Key(VK_CONTROL, up: false),
            Key(VK_C, up: false),
            Key(VK_C, up: true),
            Key(VK_CONTROL, up: true),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 } },
    };

    private static DataObject? TryBackupClipboard()
    {
        return Retry(() =>
        {
            var current = Clipboard.GetDataObject();
            if (current == null) return null;

            var copy = new DataObject();
            int saved = 0;
            var available = current.GetFormats(autoConvert: false);
            foreach (var format in BackupFormats.Where(available.Contains))
            {
                try
                {
                    var data = current.GetData(format, autoConvert: false);
                    if (data != null)
                    {
                        copy.SetData(format, data);
                        saved++;
                    }
                }
                catch
                {
                    // Some private/delay-rendered formats cannot be read; skip them.
                }
            }
            return saved > 0 ? copy : null;
        });
    }

    /// <summary>The clipboard is a shared resource and is briefly locked by other apps; retry a few times.</summary>
    private static T? Retry<T>(Func<T?> action)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { return action(); }
            catch (ExternalException) { Thread.Sleep(30); } // includes COMException (CLIPBRD_E_CANT_OPEN)
        }
        return default;
    }
}
