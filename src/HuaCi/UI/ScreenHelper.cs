using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using HuaCi.Selection;
using static HuaCi.Selection.NativeMethods;

namespace HuaCi.UI;

/// <summary>Positions windows in physical pixels so placement is correct on every monitor / DPI.</summary>
internal static class ScreenHelper
{
    public static (RECT Work, double Scale) MonitorAt(POINT p)
    {
        var monitor = MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        double scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;
        return (info.rcWork, scale);
    }

    public static IntPtr Handle(Window w) => new WindowInteropHelper(w).EnsureHandle();

    public static void MovePx(Window w, int x, int y) =>
        SetWindowPos(Handle(w), IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

    public static RECT WindowRect(Window w)
    {
        GetWindowRect(Handle(w), out var r);
        return r;
    }

    public static bool Contains(Window w, int x, int y) => w.IsVisible && WindowRect(w).Contains(x, y);

    /// <summary>Places the window below-right of the anchor (or above it when there is no room), clamped to the work area.</summary>
    public static void PlaceNear(Window w, POINT anchor, double offsetXDip, double offsetYDip)
    {
        var (work, scale) = MonitorAt(anchor);
        double width = (w.ActualWidth > 0 ? w.ActualWidth : w.Width) * scale;
        double height = (w.ActualHeight > 0 ? w.ActualHeight : 200) * scale;

        double x = anchor.X + offsetXDip * scale;
        double y = anchor.Y + offsetYDip * scale;
        if (y + height > work.Bottom)
        {
            double above = anchor.Y - height - 4 * scale;
            y = above >= work.Top ? above : Math.Max(work.Top, work.Bottom - height);
        }
        x = Math.Max(work.Left, Math.Min(x, work.Right - width));
        MovePx(w, (int)Math.Round(x), (int)Math.Round(y));
    }

    /// <summary>After the window grew, nudge it back inside the work area without otherwise moving it.</summary>
    public static void KeepInside(Window w)
    {
        var r = WindowRect(w);
        var (work, _) = MonitorAt(new POINT(r.Left + 10, r.Top + 10));
        int height = r.Bottom - r.Top, width = r.Right - r.Left;
        int x = r.Left, y = r.Top;
        if (r.Bottom > work.Bottom) y = Math.Max(work.Top, work.Bottom - height);
        if (r.Right > work.Right) x = Math.Max(work.Left, work.Right - width);
        if (x != r.Left || y != r.Top) MovePx(w, x, y);
    }

    /// <summary>Work-area height in DIPs of the monitor that contains the point.</summary>
    public static double WorkHeightDip(POINT p)
    {
        var (work, scale) = MonitorAt(p);
        return (work.Bottom - work.Top) / scale;
    }

    public static void MakeToolWindow(Window w, bool noActivate)
    {
        var hwnd = Handle(w);
        int style = GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW;
        if (noActivate) style |= WS_EX_NOACTIVATE;
        SetWindowLong(hwnd, GWL_EXSTYLE, style);
    }
}
