using System.Windows.Input;
using System.Windows.Interop;
using static HuaCi.Selection.NativeMethods;

namespace HuaCi.Selection;

[Flags]
public enum HotkeyModifiers : uint { None = 0, Alt = 1, Ctrl = 2, Shift = 4, Win = 8 }

public readonly record struct Hotkey(HotkeyModifiers Modifiers, Key Key)
{
    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join('+', parts);
    }

    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        Key.Oem3 => "`",
        _ => key.ToString(),
    };

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var mods = HotkeyModifiers.None;
        Key? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= HotkeyModifiers.Ctrl; break;
                case "alt": mods |= HotkeyModifiers.Alt; break;
                case "shift": mods |= HotkeyModifiers.Shift; break;
                case "win" or "windows": mods |= HotkeyModifiers.Win; break;
                default:
                    if (key != null) return false;
                    if (raw.Length == 1 && char.IsDigit(raw[0])) key = Key.D0 + (raw[0] - '0');
                    else if (raw == "`") key = Key.Oem3;
                    else if (Enum.TryParse<Key>(raw, true, out var k)) key = k;
                    else return false;
                    break;
            }
        }
        // 单独的字母键当全局热键会让正常打字失效，至少要一个修饰键（F1-F24 除外）
        if (key == null) return false;
        if (mods == HotkeyModifiers.None && key is not (>= Key.F1 and <= Key.F24)) return false;
        hotkey = new Hotkey(mods, key.Value);
        return true;
    }
}

/// <summary>Registers one global hotkey on a hidden message-only window.</summary>
public sealed class HotkeyManager : IDisposable
{
    private const int HotkeyId = 0x4843; // "HC"
    private const uint MOD_NOREPEAT = 0x4000;
    private readonly HwndSource _source;
    private bool _registered;

    public HotkeyManager()
    {
        var p = new HwndSourceParameters("HuaCiHotkey") { ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    public event Action? Pressed;

    public Hotkey? Current { get; private set; }

    /// <summary>Returns null on success or an error message (e.g. already taken by another app).</summary>
    public string? Register(string? text)
    {
        Unregister();
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!Hotkey.TryParse(text, out var hk)) return $"无法识别快捷键“{text}”";
        var vk = (uint)KeyInterop.VirtualKeyFromKey(hk.Key);
        if (!RegisterHotKey(_source.Handle, HotkeyId, (uint)hk.Modifiers | MOD_NOREPEAT, vk))
            return $"快捷键 {hk} 已被其他程序占用，请换一个";
        _registered = true;
        Current = hk;
        return null;
    }

    public void Unregister()
    {
        if (_registered) UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
        Current = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source.Dispose();
    }
}
