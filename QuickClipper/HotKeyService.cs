using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace QuickClipper;

public sealed class HotKeyService : IDisposable
{
    private const int HotKeyId = 0x5143;
    private const int ResetHotKeyId = 0x5144;

    private HwndSource? _source;
    private IntPtr _handle;
    private bool _hooked;

    public event EventHandler? Pressed;
    public event EventHandler? ResetPressed;

    public void Register(IntPtr windowHandle, HotKeyBinding recordHotKey, HotKeyBinding resetHotKey)
    {
        _handle = windowHandle;
        _source = HwndSource.FromHwnd(windowHandle);
        if (!_hooked)
        {
            _source.AddHook(WndProc);
            _hooked = true;
        }

        UnregisterHotKey(windowHandle, HotKeyId);
        UnregisterHotKey(windowHandle, ResetHotKeyId);
        RegisterOne(windowHandle, HotKeyId, recordHotKey);
        RegisterOne(windowHandle, ResetHotKeyId, resetHotKey);
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            UnregisterHotKey(_handle, HotKeyId);
            UnregisterHotKey(_handle, ResetHotKeyId);
        }

        if (_hooked)
        {
            _source?.RemoveHook(WndProc);
        }
    }

    private static void RegisterOne(IntPtr windowHandle, int id, HotKeyBinding hotKey)
    {
        if (!hotKey.IsValid || !RegisterHotKey(windowHandle, id, hotKey.Modifiers, hotKey.Key))
        {
            throw new InvalidOperationException($"Could not register hotkey {hotKey.Label}.");
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312 && wParam.ToInt32() == HotKeyId)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }
        else if (msg == 0x0312 && wParam.ToInt32() == ResetHotKeyId)
        {
            handled = true;
            ResetPressed?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
