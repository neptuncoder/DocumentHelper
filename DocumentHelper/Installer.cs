using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace DocumentHelper;

/// <summary>
/// Per-user install without an installer: the shareable build is a single .exe that people download and run.
/// "Installing" copies it to %LOCALAPPDATA%\Programs\DocumentHelper (so it keeps working if the download is
/// deleted), starts it with Windows and adds a Start menu shortcut. No administrator rights are needed.
/// </summary>
internal static class Installer
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "DocumentHelper";

    public static string InstallDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DocumentHelper");

    public static string InstalledExe { get; } = Path.Combine(InstallDir, "DocumentHelper.exe");

    private static string ShortcutPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Document Helper.lnk");

    private static string CurrentExe => Environment.ProcessPath!;

    /// <summary>True for the single-file build, which can simply copy itself (a normal build is many files).</summary>
#pragma warning disable IL3000 // Location is empty inside a single-file app; that is exactly what is being detected.
    public static bool CanCopySelf => string.IsNullOrEmpty(typeof(Installer).Assembly.Location);
#pragma warning restore IL3000

    public static bool IsRunningFromInstallDir =>
        string.Equals(Path.GetFullPath(CurrentExe), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

    public static bool IsAutostartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) is string;
    }

    /// <summary>
    /// Turns "start with Windows" on or off. Turning it on from a downloaded single-file copy installs it
    /// first, so Windows starts the permanent copy rather than the file in Downloads.
    /// Returns the exe that will be started with Windows.
    /// </summary>
    public static string? SetAutostart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (!enabled)
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
            return null;
        }

        string exe = CurrentExe;
        if (CanCopySelf && !IsRunningFromInstallDir)
        {
            Directory.CreateDirectory(InstallDir);
            File.Copy(CurrentExe, InstalledExe, overwrite: true);
            exe = InstalledExe;
        }
        key.SetValue(RunValueName, $"\"{exe}\"");
        TryCreateShortcut(exe);
        return exe;
    }

    /// <summary>
    /// Removes everything the app created: autostart entry, Start menu shortcut, settings, cached browser data
    /// and the installed copy. Files still in use are deleted by a helper command after the app has exited.
    /// </summary>
    public static void Uninstall()
    {
        SetAutostart(false);
        try { File.Delete(ShortcutPath); } catch (IOException) { }

        var folders = new[]
        {
            InstallDir,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DocumentHelper"),
            Path.GetDirectoryName(AppSettings.FilePath)!,
        };
        // Wait a few seconds (for this app and its browser processes to exit), then delete the folders.
        string commands = "ping 127.0.0.1 -n 4 > nul & " + string.Join(" & ", folders.Select(f => $"rmdir /s /q \"{f}\""));
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c {commands}")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    private static void TryCreateShortcut(string exe)
    {
        try
        {
            // WScript.Shell is the standard way to write .lnk files without extra libraries.
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(ShortcutPath);
            shortcut.TargetPath = exe;
            shortcut.WorkingDirectory = Path.GetDirectoryName(exe);
            shortcut.Description = "Turkish meaning of the selected word";
            shortcut.Save();
        }
        catch (Exception ex)
        {
            App.Log("Creating the Start menu shortcut failed: " + ex.Message);
        }
    }
}
