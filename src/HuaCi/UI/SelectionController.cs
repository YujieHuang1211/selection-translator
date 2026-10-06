using System.Diagnostics;
using System.Windows.Threading;
using HuaCi.Core;
using HuaCi.Selection;
using static HuaCi.Selection.NativeMethods;

namespace HuaCi.UI;

/// <summary>
/// Glue between the global input hooks and the UI: shows the "译" icon after a selection,
/// grabs the text on click / hotkey, and manages the translation popup's lifetime.
/// </summary>
public sealed class SelectionController : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly GlobalInputHook _hook;
    private readonly SelectionGestureDetector _detector;
    private readonly HotkeyManager _hotkeys = new();
    private readonly FloatingIcon _icon = new();
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private TranslatePopup? _popup;
    private bool _paused;
    private bool _grabbing;

    public SelectionController()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _detector = new SelectionGestureDetector(GetDoubleClickTime(), Math.Max(4, GetSystemMetrics(36 /* SM_CXDOUBLECLK */) / 2));
        _hook = new GlobalInputHook(e => _dispatcher.BeginInvoke(DispatcherPriority.Input, () => OnInput(e)));
        _icon.Clicked += async () => await TranslateSelectionAsync(_icon.Anchor, fromHotkey: false);
        _hotkeys.Pressed += async () => await OnHotkeyAsync();
    }

    public event Action<bool>? PausedChanged;

    public bool Paused
    {
        get => _paused;
        set
        {
            if (_paused == value) return;
            _paused = value;
            if (value) _icon.HideNow();
            PausedChanged?.Invoke(value);
        }
    }

    public TranslatePopup Popup => _popup ??= CreatePopup();

    public void Start()
    {
        _hook.Start();
        ApplySettings(App.SettingsStore.Current);
    }

    /// <summary>Re-reads settings; returns an error message if the hotkey could not be registered.</summary>
    public string? ApplySettings(AppSettings s)
    {
        _detector.RequireTextCursor = s.RequireTextCursor;
        if (!s.SelectionIconEnabled) _icon.HideNow();
        return _hotkeys.Register(s.Hotkey);
    }

    private TranslatePopup CreatePopup()
    {
        var popup = new TranslatePopup();
        popup.ViewModel.OpenSettingsRequested += () =>
        {
            popup.HidePopup(force: true);
            App.ShowSettings();
        };
        return popup;
    }

    private void OnInput(InputEvent e)
    {
        switch (e.Kind)
        {
            case InputKind.LeftDown:
                if (IsOwnWindowAt(e.X, e.Y))
                {
                    _detector.Reset();
                    return;
                }
                _icon.HideNow();
                _popup?.HidePopup(force: false);
                _detector.OnLeftDown(e);
                break;

            case InputKind.LeftUp:
                if (IsOwnWindowAt(e.X, e.Y)) return;
                var gesture = _detector.OnLeftUp(e);
                if (gesture != null) OnSelectionGesture(gesture.Value);
                break;

            case InputKind.OtherButtonDown:
                _icon.HideNow();
                if (!IsOwnWindowAt(e.X, e.Y)) _popup?.HidePopup(force: false);
                break;

            case InputKind.Wheel:
                if (!ScreenHelper.Contains(_icon, e.X, e.Y)) _icon.HideNow();
                break;

            case InputKind.KeyDown:
                _icon.HideNow();
                if (e.VirtualKey == VK_ESCAPE && _popup is { IsVisible: true } popup && (!popup.IsPinned || popup.IsActive))
                    popup.HidePopup(force: true);
                break;
        }
    }

    private void OnSelectionGesture(SelectionGesture gesture)
    {
        var s = App.SettingsStore.Current;
        if (Paused || !s.SelectionIconEnabled || _grabbing) return;

        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || GetProcessId(foreground) == _ownPid) return;
        var process = GetProcessName(foreground);
        if (s.BlacklistProcesses.Any(b => StripExe(b).Equals(process, StringComparison.OrdinalIgnoreCase)))
            return;

        _icon.ShowAt(new POINT(gesture.X, gesture.Y), s.IconAutoHideSeconds);
    }

    private async Task OnHotkeyAsync()
    {
        if (_hotkeys.Current is { } hk && (hk.Modifiers.HasFlag(HotkeyModifiers.Alt) || hk.Modifiers.HasFlag(HotkeyModifiers.Win)))
            TextGrabber.SuppressModifierTap();
        GetCursorPos(out var cursor);
        await TranslateSelectionAsync(cursor, fromHotkey: true);
    }

    private async Task TranslateSelectionAsync(POINT anchor, bool fromHotkey)
    {
        if (_grabbing) return;
        _icon.HideNow();
        var foreground = GetForegroundWindow();
        string text = "";

        if (GetProcessId(foreground) != _ownPid)
        {
            _grabbing = true;
            try
            {
                text = await App.Grabber.GrabAsync(foreground);
            }
            catch (Exception ex)
            {
                App.Log("获取选中文字失败", ex);
            }
            finally
            {
                _grabbing = false;
            }
        }

        var popup = Popup;
        popup.ShowAt(anchor);
        if (string.IsNullOrWhiteSpace(text))
        {
            popup.ViewModel.ShowEmpty(fromHotkey
                ? "没有检测到选中的文字，可以直接在上面输入要翻译的内容"
                : "没能读取到选中的文字（该程序可能不支持），可以直接在上面输入或粘贴");
            popup.FocusSourceInput();
            return;
        }
        await popup.ViewModel.StartAsync(text);
    }

    /// <summary>Opens the popup with a cached result (from the history window).</summary>
    public void ShowCached(string text, string translation, Translation.AnalysisResult? analysis)
    {
        GetCursorPos(out var cursor);
        Popup.ShowAt(cursor);
        Popup.ViewModel.ShowCached(text, translation, analysis);
        Popup.Activate();
    }

    private static string StripExe(string name)
    {
        name = name.Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private bool IsOwnWindowAt(int x, int y)
    {
        var hwnd = WindowFromPoint(new POINT(x, y));
        return hwnd != IntPtr.Zero && GetProcessId(hwnd) == _ownPid;
    }

    public void Dispose()
    {
        _hook.Dispose();
        _hotkeys.Dispose();
        try
        {
            _icon.Close();
            _popup?.Close();
        }
        catch (InvalidOperationException ex)
        {
            Debug.WriteLine(ex);
        }
    }
}
