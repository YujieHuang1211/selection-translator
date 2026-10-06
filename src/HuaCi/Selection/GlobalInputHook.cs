using System.Runtime.InteropServices;
using static HuaCi.Selection.NativeMethods;

namespace HuaCi.Selection;

public enum InputKind { LeftDown, LeftUp, OtherButtonDown, Wheel, KeyDown }

public readonly record struct InputEvent(InputKind Kind, int X, int Y, uint Time, bool IsTextCursor, bool Shift, int VirtualKey);

/// <summary>
/// Low-level mouse + keyboard hooks on a dedicated thread with its own message loop,
/// so a busy UI thread can never make the system mouse lag. Events are handed to the callback
/// (still on the hook thread — the receiver must marshal to the UI thread and return quickly).
/// </summary>
public sealed class GlobalInputHook : IDisposable
{
    private readonly Action<InputEvent> _callback;
    private readonly HookProc _mouseProc;
    private readonly HookProc _keyboardProc;
    private readonly IntPtr _ibeamCursor = LoadCursor(IntPtr.Zero, IDC_IBEAM);
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;

    public GlobalInputHook(Action<InputEvent> callback)
    {
        _callback = callback;
        _mouseProc = MouseProc;
        _keyboardProc = KeyboardProc;
    }

    public void Start()
    {
        if (_thread != null) return;
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            var module = GetModuleHandle(null);
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
            _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, module, 0);
            ready.Set();
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) { /* pump for the hooks */ }
            UnhookWindowsHookEx(_mouseHook);
            UnhookWindowsHookEx(_keyboardHook);
        })
        {
            IsBackground = true,
            Name = "HuaCi input hook",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
        ready.Wait(2000);
        if (_mouseHook == IntPtr.Zero) throw new InvalidOperationException("无法安装全局鼠标钩子");
    }

    public bool IsTextCursor()
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        return GetCursorInfo(ref info) && info.hCursor == _ibeamCursor;
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            if (msg != WM_MOUSEMOVE)
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                InputKind? kind = msg switch
                {
                    WM_LBUTTONDOWN => InputKind.LeftDown,
                    WM_LBUTTONUP => InputKind.LeftUp,
                    WM_RBUTTONDOWN or WM_MBUTTONDOWN => InputKind.OtherButtonDown,
                    WM_MOUSEWHEEL or WM_MOUSEHWHEEL => InputKind.Wheel,
                    _ => null,
                };
                if (kind != null)
                {
                    bool ibeam = kind is InputKind.LeftDown or InputKind.LeftUp && IsTextCursor();
                    bool shift = kind == InputKind.LeftDown && IsKeyDown(VK_SHIFT);
                    SafeInvoke(new InputEvent(kind.Value, data.pt.X, data.pt.Y, data.time, ibeam, shift, 0));
                }
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && ((int)wParam == WM_KEYDOWN || (int)wParam == WM_SYSKEYDOWN))
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            // 忽略我们自己 SendInput 注入的按键（LLKHF_INJECTED = 0x10）
            if ((data.flags & 0x10) == 0)
                SafeInvoke(new InputEvent(InputKind.KeyDown, 0, 0, data.time, false, false, (int)data.vkCode));
        }
        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private void SafeInvoke(InputEvent e)
    {
        try { _callback(e); }
        catch { /* never let an exception escape a hook */ }
    }

    public void Dispose()
    {
        if (_thread == null) return;
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
        _thread = null;
    }
}
