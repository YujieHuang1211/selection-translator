using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using static HuaCi.Selection.NativeMethods;

namespace HuaCi.UI;

/// <summary>The small "译" bubble shown next to the cursor after a selection. Never takes focus.</summary>
public partial class FloatingIcon : Window
{
    private readonly DispatcherTimer _hideTimer = new();
    private TimeSpan _lifetime = TimeSpan.FromSeconds(4);

    public FloatingIcon()
    {
        InitializeComponent();
        _hideTimer.Tick += (_, _) => FadeOut();
        SourceInitialized += (_, _) =>
        {
            ScreenHelper.MakeToolWindow(this, noActivate: true);
            HwndSource.FromHwnd(ScreenHelper.Handle(this))?.AddHook(WndProc);
        };
    }

    public event Action? Clicked;

    /// <summary>Selection end point in physical pixels, used to place the popup.</summary>
    public POINT Anchor { get; private set; }

    public void ShowAt(POINT anchor, double lifetimeSeconds)
    {
        Anchor = anchor;
        _lifetime = TimeSpan.FromSeconds(Math.Max(1, lifetimeSeconds));
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        if (!IsVisible) Show();
        // 光标右下方，避免挡住刚选中的文字
        ScreenHelper.PlaceNear(this, anchor, 6, 8);

        var pop = new DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } };
        Scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        Scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        RestartTimer(_lifetime);
    }

    public void HideNow()
    {
        _hideTimer.Stop();
        if (IsVisible) Hide();
    }

    private void FadeOut()
    {
        _hideTimer.Stop();
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) =>
        {
            if (Opacity == 0) Hide();
        };
        BeginAnimation(OpacityProperty, fade);
    }

    private void RestartTimer(TimeSpan after)
    {
        _hideTimer.Stop();
        _hideTimer.Interval = after;
        _hideTimer.Start();
    }

    private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        HideNow();
        Clicked?.Invoke();
    }

    private void Root_MouseEnter(object sender, MouseEventArgs e)
    {
        _hideTimer.Stop();
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        var grow = new DoubleAnimation(1.12, TimeSpan.FromMilliseconds(100));
        Scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        Scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    private void Root_MouseLeave(object sender, MouseEventArgs e)
    {
        var shrink = new DoubleAnimation(1, TimeSpan.FromMilliseconds(100));
        Scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        Scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        RestartTimer(TimeSpan.FromSeconds(1.5));
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_MOUSEACTIVATE)
        {
            // 点击图标不激活窗口，原程序的选区和焦点保持不变
            handled = true;
            return new IntPtr(MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }
}
