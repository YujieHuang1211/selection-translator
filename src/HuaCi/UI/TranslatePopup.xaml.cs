using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Animation;
using HuaCi.Core;
using static HuaCi.Selection.NativeMethods;

namespace HuaCi.UI;

public partial class TranslatePopup : Window
{
    public static readonly IValueConverter PinTipConverter =
        new FuncConverter(v => v is true ? "取消固定" : "固定窗口（不会自动关闭，可拖动）");

    public static readonly IValueConverter LargerFont = new FuncConverter(v => v is double d ? d + 1.5 : 15.5);

    private const double CollapsedSourceHeight = 70;
    private int _showVersion;

    public TranslatePopup()
    {
        InitializeComponent();
        ViewModel = new TranslatePopupViewModel();
        DataContext = ViewModel;
        ViewModel.PropertyChanged += OnViewModelChanged;
        SourceInitialized += (_, _) => ScreenHelper.MakeToolWindow(this, noActivate: false);
        SizeChanged += (_, _) =>
        {
            if (IsVisible) ScreenHelper.KeepInside(this);
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                HidePopup(force: true);
            }
        };
        App.SettingsStore.Changed += _ => Dispatcher.BeginInvoke(ApplySettings);
        ApplySettings();
        UpdateSourceHeight();
    }

    public TranslatePopupViewModel ViewModel { get; }

    public bool IsPinned => ViewModel.IsPinned;

    /// <summary>Shows the popup next to the anchor point (physical pixels). Keeps position if pinned and already visible.</summary>
    public void ShowAt(POINT anchor)
    {
        bool reposition = !IsVisible || !IsPinned;
        _showVersion++;
        App.Speech?.Warmup();
        BeginAnimation(OpacityProperty, null);
        Scroller.MaxHeight = Math.Min(640, ScreenHelper.WorkHeightDip(anchor) * 0.82) - 50;

        if (!IsVisible)
        {
            Opacity = 0;
            Show();
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130)));
        }
        else
        {
            Opacity = 1;
        }
        if (reposition)
        {
            UpdateLayout();
            // 窗口外留了 12px 阴影边距，往左上偏移让卡片贴近光标
            ScreenHelper.PlaceNear(this, anchor, -14, 10);
        }
    }

    /// <summary>Hides the popup. Unless <paramref name="force"/>, a pinned popup stays.</summary>
    public void HidePopup(bool force)
    {
        if (!IsVisible) return;
        if (IsPinned && !force) return;
        if (force) ViewModel.IsPinned = false;
        App.Speech?.Stop();
        var version = _showVersion;
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(90));
        fade.Completed += (_, _) =>
        {
            if (version != _showVersion) return; // 淡出过程中又被重新打开了
            Hide();
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
        };
        BeginAnimation(OpacityProperty, fade);
    }

    public void FocusSourceInput()
    {
        Activate();
        SourceBox.Focus();
        SourceBox.CaretIndex = SourceBox.Text.Length;
    }

    private void ApplySettings()
    {
        Width = Math.Clamp(App.SettingsStore.Current.PopupWidth, 360, 720) + 24;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TranslatePopupViewModel.IsSourceExpanded) or nameof(TranslatePopupViewModel.SourceText))
            UpdateSourceHeight();
    }

    private void UpdateSourceHeight()
    {
        bool collapse = ViewModel.IsSourceLong && !ViewModel.IsSourceExpanded;
        SourceBox.MaxHeight = collapse ? CollapsedSourceHeight : double.PositiveInfinity;
        SourceToggleText.Text = ViewModel.IsSourceExpanded ? "收起原文" : "展开原文";
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1) return;
        var before = ScreenHelper.WindowRect(this);
        try { DragMove(); } catch (InvalidOperationException) { return; }
        var after = ScreenHelper.WindowRect(this);
        // 拖动过的窗口自动固定，方便对照阅读
        if (before.Left != after.Left || before.Top != after.Top) ViewModel.IsPinned = true;
    }

    private void TargetButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = TargetButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var lang in LanguageInfo.All)
        {
            if (LanguageInfo.SameLanguage(lang.Code, ViewModel.SourceLang) && !lang.Code.StartsWith("zh")) continue;
            var item = new MenuItem
            {
                Header = "译成" + lang.DisplayName,
                IsCheckable = true,
                IsChecked = lang.Code == LanguageInfo.Find(ViewModel.EffectiveTarget)?.Code,
            };
            var code = lang.Code;
            item.Click += async (_, _) => await ViewModel.ChangeTargetAsync(code);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void PinButton_Click(object sender, RoutedEventArgs e) => ViewModel.IsPinned = !ViewModel.IsPinned;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => HidePopup(force: true);

    private async void SourceBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            await ViewModel.StartAsync(SourceBox.Text);
        }
        else if (e.Key == Key.Enter)
        {
            // Shift+Enter 换行
            e.Handled = true;
            var i = SourceBox.CaretIndex;
            SourceBox.Text = SourceBox.Text.Insert(i, "\n");
            SourceBox.CaretIndex = i + 1;
        }
    }

    private sealed class FuncConverter : IValueConverter
    {
        private readonly Func<object?, object> _f;
        public FuncConverter(Func<object?, object> f) => _f = f;
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => _f(value);
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
