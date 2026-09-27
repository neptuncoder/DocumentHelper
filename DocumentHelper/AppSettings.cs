using System.IO;
using System.Text.Json;
using System.Windows.Input;

namespace DocumentHelper;

/// <summary>User settings, stored as JSON in %APPDATA%\DocumentHelper\settings.json (edit it and restart the app).</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DocumentHelper", "settings.json");

    /// <summary>
    /// Global hotkey, e.g. "F10" or "Ctrl+Shift+Y". While the app runs, other programs no longer receive it.
    /// Avoid Ctrl+Alt combinations: on Turkish keyboards Ctrl+Alt equals AltGr, which types @, € and ₺.
    /// </summary>
    public string Hotkey { get; set; } = "F10";

    public bool UseTureng { get; set; } = true;

    public bool UseCambridge { get; set; } = true;

    /// <summary>Set after the first start, when the user was asked whether to start with Windows.</summary>
    public bool FirstRunDone { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception)
        {
            // Corrupt file: fall back to defaults rather than refusing to start.
        }
        var defaults = new AppSettings();
        defaults.Save();
        return defaults;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }

    public bool TryGetHotkey(out ModifierKeys modifiers, out Key key)
    {
        modifiers = ModifierKeys.None;
        key = Key.None;
        try
        {
            if (new KeyGestureConverter().ConvertFromInvariantString(Hotkey) is KeyGesture gesture)
            {
                modifiers = gesture.Modifiers;
                key = gesture.Key;
                return true;
            }
        }
        catch (Exception)
        {
            // Invalid hotkey text.
        }
        return false;
    }
}
