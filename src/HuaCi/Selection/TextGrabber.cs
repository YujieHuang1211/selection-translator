using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using HuaCi.Core;
using static HuaCi.Selection.NativeMethods;

namespace HuaCi.Selection;

/// <summary>
/// Reads the currently selected text in another application:
/// UI Automation first (silent), then a simulated copy with the clipboard saved and restored.
/// </summary>
public sealed class TextGrabber
{
    private static readonly string[] CopyFirstWindowClasses =
    {
        "Chrome_WidgetWin_1",   // Chrome / Edge / Brave / Electron apps（VS Code、Slack…），含浏览器里的 PDF
        "MozillaWindowClass",   // Firefox
    };

    private readonly Func<AppSettings> _settings;

    public TextGrabber(Func<AppSettings> settings) => _settings = settings;

    public async Task<string> GrabAsync(IntPtr window)
    {
        var className = GetClassName(window);
        var process = GetProcessName(window);

        if (!CopyFirstWindowClasses.Contains(className))
        {
            var uia = await TryUiaAsync(TimeSpan.FromMilliseconds(200));
            if (!string.IsNullOrWhiteSpace(uia)) return uia;
        }

        var copyKeys = _settings().CopyKeyOverrides.TryGetValue(process, out var keys) ? keys : "Ctrl+C";
        return await CopyViaClipboardAsync(copyKeys) ?? "";
    }

    private static async Task<string?> TryUiaAsync(TimeSpan timeout)
    {
        var task = Task.Run(() =>
        {
            try
            {
                var element = AutomationElement.FocusedElement;
                if (element == null || !element.TryGetCurrentPattern(TextPattern.Pattern, out var p)) return null;
                var ranges = ((TextPattern)p).GetSelection();
                var sb = new StringBuilder();
                foreach (var range in ranges)
                {
                    var text = range.GetText(TextNormalizer.MaxLength * 2);
                    if (sb.Length > 0 && text.Length > 0) sb.Append('\n');
                    sb.Append(text);
                }
                return sb.ToString();
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or ArgumentException)
            {
                return null;
            }
        });
        var done = await Task.WhenAny(task, Task.Delay(timeout));
        return done == task ? task.Result : null;
    }

    /// <summary>Simulates the copy shortcut and reads the result, restoring the previous clipboard afterwards.</summary>
    private static async Task<string?> CopyViaClipboardAsync(string copyKeys)
    {
        await WaitForModifiersReleasedAsync(TimeSpan.FromMilliseconds(1500));

        var snapshot = ClipboardGuard.Capture();
        var before = GetClipboardSequenceNumber();
        SendShortcut(copyKeys);

        var deadline = DateTime.UtcNow.AddMilliseconds(600);
        while (GetClipboardSequenceNumber() == before && DateTime.UtcNow < deadline)
            await Task.Delay(15);

        if (GetClipboardSequenceNumber() == before) return null; // 没有选中内容，剪贴板未被改动

        string? text = null;
        for (int i = 0; i < 5 && string.IsNullOrEmpty(text); i++)
        {
            await Task.Delay(i == 0 ? 30 : 50); // 一些程序先清空再延迟写入
            text = ClipboardGuard.GetText();
        }

        ClipboardGuard.Restore(snapshot);
        return text;
    }

    private static async Task WaitForModifiersReleasedAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline &&
               (IsKeyDown(VK_CONTROL) || IsKeyDown(VK_MENU) || IsKeyDown(VK_SHIFT) || IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN)))
        {
            await Task.Delay(15);
        }
    }

    /// <summary>Sends e.g. "Ctrl+C", "Ctrl+Shift+C" or "Ctrl+Insert" with SendInput.</summary>
    public static void SendShortcut(string shortcut)
    {
        var mods = new List<ushort>();
        ushort key = VK_C;
        foreach (var part in shortcut.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": mods.Add(VK_CONTROL); break;
                case "shift": mods.Add(VK_SHIFT); break;
                case "alt": mods.Add(VK_MENU); break;
                case "insert" or "ins": key = VK_INSERT; break;
                default:
                    if (part.Length == 1) key = char.ToUpperInvariant(part[0]);
                    break;
            }
        }

        var inputs = new List<INPUT>();
        foreach (var m in mods) inputs.Add(Key(m, false));
        inputs.Add(Key(key, false));
        inputs.Add(Key(key, true));
        for (int i = mods.Count - 1; i >= 0; i--) inputs.Add(Key(mods[i], true));
        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    /// <summary>Taps an unassigned key so releasing Alt/Win after our hotkey does not open the menu bar / Start menu.</summary>
    public static void SuppressModifierTap()
    {
        var inputs = new[] { Key(VK_UNASSIGNED, false), Key(VK_UNASSIGNED, true) };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 } },
    };
}
