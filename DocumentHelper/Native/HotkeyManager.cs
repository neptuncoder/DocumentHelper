using System.Windows.Input;
using System.Windows.Interop;

namespace DocumentHelper.Native;

/// <summary>
/// Registers a system-wide hotkey and raises <see cref="Pressed"/> when it is hit in any application.
/// </summary>
internal sealed class HotkeyManager : IDisposable
{
    private const int HotkeyId = 0xB001;

    private readonly HwndSource _source;
    private bool _registered;

    public event EventHandler? Pressed;

    public HotkeyManager()
    {
        // Message-only window (HWND_MESSAGE parent) that just receives WM_HOTKEY.
        var parameters = new HwndSourceParameters("DocumentHelperHotkeySink")
        {
            ParentWindow = new IntPtr(-3),
            WindowStyle = 0,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public bool Register(ModifierKeys modifiers, Key key)
    {
        Unregister();

        uint mods = NativeMethods.MOD_NOREPEAT;
        if (modifiers.HasFlag(ModifierKeys.Alt)) mods |= NativeMethods.MOD_ALT;
        if (modifiers.HasFlag(ModifierKeys.Control)) mods |= NativeMethods.MOD_CONTROL;
        if (modifiers.HasFlag(ModifierKeys.Shift)) mods |= NativeMethods.MOD_SHIFT;
        if (modifiers.HasFlag(ModifierKeys.Windows)) mods |= NativeMethods.MOD_WIN;

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        _registered = NativeMethods.RegisterHotKey(_source.Handle, HotkeyId, mods, vk);
        return _registered;
    }

    public void Unregister()
    {
        if (_registered)
        {
            NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
            _registered = false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
