using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace QuickClipper;

public sealed class HotKeyService : IDisposable
{
    private const int HotKeyId = 0x5143;
    private const int ResetHotKeyId = 0x5144;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint VkR = 0x52;
    private const uint Vk4 = 0x34;

    private HwndSource? _source;
    private IntPtr _handle;

    public event EventHandler? Pressed;
    public event EventHandler? ResetPressed;

    public void Register(IntPtr windowHandle)
    {
        _handle = windowHandle;
        _source = HwndSource.FromHwnd(windowHandle);
        _source.AddHook(WndProc);
        RegisterHotKey(windowHandle, HotKeyId, ModWin | ModShift, VkR);
        RegisterHotKey(windowHandle, ResetHotKeyId, ModWin | ModShift, Vk4);
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            UnregisterHotKey(_handle, HotKeyId);
            UnregisterHotKey(_handle, ResetHotKeyId);
        }

        _source?.RemoveHook(WndProc);
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
