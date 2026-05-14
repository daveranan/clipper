using System.Windows.Input;

namespace QuickClipper;

public sealed class HotKeyBinding
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    public uint Modifiers { get; set; }

    public uint Key { get; set; }

    public string Label => ToLabel();

    public bool IsValid => Modifiers != 0 && Key != 0;

    public bool SameAs(HotKeyBinding other) => Modifiers == other.Modifiers && Key == other.Key;

    public static HotKeyBinding DefaultRecord() => new() { Modifiers = ModWin | ModShift, Key = 0x52 };

    public static HotKeyBinding DefaultReset() => new() { Modifiers = ModWin | ModShift, Key = 0x34 };

    public static HotKeyBinding FromKeyEvent(System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        if (key is System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt or System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl or System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift or System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin)
        {
            return new HotKeyBinding();
        }

        return new HotKeyBinding
        {
            Modifiers = ToHotKeyModifiers(Keyboard.Modifiers) | CurrentWindowsModifier(),
            Key = (uint)KeyInterop.VirtualKeyFromKey(key)
        };
    }

    private static uint CurrentWindowsModifier() =>
        Keyboard.IsKeyDown(System.Windows.Input.Key.LWin) || Keyboard.IsKeyDown(System.Windows.Input.Key.RWin)
            ? ModWin
            : 0;

    private static uint ToHotKeyModifiers(ModifierKeys modifiers)
    {
        uint result = 0;
        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            result |= ModAlt;
        }

        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            result |= ModControl;
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            result |= ModShift;
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            result |= ModWin;
        }

        return result;
    }

    private string ToLabel()
    {
        var parts = new List<string>();
        if ((Modifiers & ModControl) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((Modifiers & ModAlt) != 0)
        {
            parts.Add("Alt");
        }

        if ((Modifiers & ModWin) != 0)
        {
            parts.Add("Win");
        }

        if ((Modifiers & ModShift) != 0)
        {
            parts.Add("Shift");
        }

        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    private static string KeyName(uint key)
    {
        if (key >= 0x30 && key <= 0x39)
        {
            return ((char)key).ToString();
        }

        if (key >= 0x41 && key <= 0x5A)
        {
            return ((char)key).ToString();
        }

        return KeyInterop.KeyFromVirtualKey((int)key).ToString();
    }
}
