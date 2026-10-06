using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HuaCi.Core;
using HuaCi.Selection;
using HuaCi.Translation;
using Wpf.Ui.Controls;

namespace HuaCi.UI;

public partial class SettingsWindow : FluentWindow
{
    private readonly AppSettings _settings;
    private List<ModelInfo> _models = new();
    private string _hotkey;

    public SettingsWindow()
    {
        InitializeComponent();
        _settings = App.SettingsStore.Current.Clone();
        _hotkey = _settings.Hotkey;
        LoadValues();
        Loaded += async (_, _) => await LoadModelsAsync();
    }

    private void LoadValues()
    {
        var s = _settings;
        WelcomeBar.IsOpen = string.IsNullOrWhiteSpace(s.ApiKey);
        WelcomeBar.Visibility = WelcomeBar.IsOpen ? Visibility.Visible : Visibility.Collapsed;
        ApiKeyBox.Password = s.ApiKey;
        ModelBox.Text = s.Model;

        foreach (var box in new[] { TargetBox, FallbackBox, ExplainBox }) box.ItemsSource = LanguageInfo.All;
        TargetBox.SelectedValue = LanguageInfo.Find(s.TargetLanguage)?.Code;
        FallbackBox.SelectedValue = LanguageInfo.Find(s.FallbackLanguage)?.Code;
        ExplainBox.SelectedValue = LanguageInfo.Find(s.ExplainLanguage)?.Code;
        LevelBox.ItemsSource = Prompts.VocabLevels.Select(l => new { l.Code, l.Name }).ToList();
        LevelBox.SelectedValue = s.VocabLevel;

        IconSwitch.IsChecked = s.SelectionIconEnabled;
        TextCursorSwitch.IsChecked = s.RequireTextCursor;
        IconSecondsSlider.Value = s.IconAutoHideSeconds;
        HotkeyBox.Text = s.Hotkey;
        BlacklistBox.Text = string.Join(", ", s.BlacklistProcesses);

        UsRadio.IsChecked = s.EnglishAccent != "UK";
        UkRadio.IsChecked = s.EnglishAccent == "UK";
        RateSlider.Value = s.SpeechRate;
        OfflineSwitch.IsChecked = s.OfflineSpeechFallback;

        ThemeBox.SelectedValue = s.Theme;
        FontSlider.Value = s.FontSize;
        WidthSlider.Value = s.PopupWidth;

        bool autoStart;
        try { autoStart = AutoStart.IsEnabled(); } catch { autoStart = s.AutoStart; }
        AutoStartSwitch.IsChecked = autoStart;
        DataDirText.Text = App.DataDirectory;
    }

    private async Task LoadModelsAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            _models = (await App.OpenRouter.ListModelsAsync(cts.Token))
                .OrderBy(m => m.Id == AppSettings.DefaultModel ? 0 : 1)
                .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var text = ModelBox.Text;
            ModelBox.ItemsSource = _models;
            ModelBox.Text = text;
            UpdateModelInfo(text);
        }
        catch (Exception ex)
        {
            ModelInfoText.Text = "暂时无法获取模型列表（" + ex.Message + "），也可以直接输入模型 ID。";
        }
    }

    private void UpdateModelInfo(string id)
    {
        var m = _models.FirstOrDefault(x => x.Id == id);
        if (m != null) ModelInfoText.Text = $"{m.Name} · {m.PriceText} · 上下文 {m.ContextLength / 1000}K";
    }

    private void ModelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelBox.SelectedItem is ModelInfo m)
        {
            // 选中后在编辑框里显示 ID 而不是对象名
            Dispatcher.BeginInvoke(() => ModelBox.Text = m.Id);
            UpdateModelInfo(m.Id);
        }
    }

    private void ResetModel_Click(object sender, RoutedEventArgs e)
    {
        ModelBox.SelectedItem = _models.FirstOrDefault(m => m.Id == AppSettings.DefaultModel);
        ModelBox.Text = AppSettings.DefaultModel;
        UpdateModelInfo(AppSettings.DefaultModel);
    }

    private string SelectedModel()
    {
        var text = (ModelBox.Text ?? "").Trim();
        if (ModelBox.SelectedItem is ModelInfo m && (text.Length == 0 || text == m.Name)) return m.Id;
        return text.Length == 0 ? AppSettings.DefaultModel : text;
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        var key = ApiKeyBox.Password?.Trim() ?? "";
        TestResult.Visibility = Visibility.Visible;
        if (key.Length == 0)
        {
            TestResult.Text = "请先填入 API Key";
            return;
        }
        TestButton.IsEnabled = false;
        TestResult.Text = "正在测试…";
        try
        {
            var client = new OpenRouterClient(() => key);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var sw = Stopwatch.StartNew();
            var error = await client.TestAsync(SelectedModel(), cts.Token);
            TestResult.Text = error == null ? $"✓ 连接成功（{sw.ElapsedMilliseconds} ms）" : "✗ " + error;
        }
        catch (OperationCanceledException)
        {
            TestResult.Text = "✗ 连接超时，请检查网络";
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Back or Key.Delete)
        {
            _hotkey = "";
            HotkeyBox.Text = "";
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        var mods = HotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mods |= HotkeyModifiers.Ctrl;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= HotkeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= HotkeyModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= HotkeyModifiers.Win;

        var text = new Hotkey(mods, key).ToString();
        if (Hotkey.TryParse(text, out _))
        {
            _hotkey = text;
            HotkeyBox.Text = text;
            HotkeyDesc.Text = "选中文字后按下即可直接翻译；没有选中时会打开输入框。按 Backspace 清除";
        }
        else
        {
            HotkeyDesc.Text = "快捷键需要包含 Ctrl / Alt / Shift / Win 中至少一个";
        }
    }

    private async void PreviewVoice_Click(object sender, RoutedEventArgs e)
    {
        if (App.Speech == null) return;
        // 先临时应用语速/口音再试听
        var current = App.SettingsStore.Current;
        var oldRate = current.SpeechRate;
        var oldAccent = current.EnglishAccent;
        current.SpeechRate = RateSlider.Value;
        current.EnglishAccent = UkRadio.IsChecked == true ? "UK" : "US";
        try
        {
            await App.Speech.SpeakAsync(this, "Hello! This is how selected text will sound.", "en");
        }
        finally
        {
            current.SpeechRate = oldRate;
            current.EnglishAccent = oldAccent;
        }
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", App.DataDirectory) { UseShellExecute = true }); }
        catch (Exception ex) { App.Log("打开数据文件夹失败", ex); }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = _settings;
        s.ApiKey = ApiKeyBox.Password?.Trim() ?? "";
        s.Model = SelectedModel();
        s.TargetLanguage = TargetBox.SelectedValue as string ?? s.TargetLanguage;
        s.FallbackLanguage = FallbackBox.SelectedValue as string ?? s.FallbackLanguage;
        s.ExplainLanguage = ExplainBox.SelectedValue as string ?? s.ExplainLanguage;
        s.VocabLevel = LevelBox.SelectedValue as string ?? s.VocabLevel;
        s.SelectionIconEnabled = IconSwitch.IsChecked == true;
        s.RequireTextCursor = TextCursorSwitch.IsChecked == true;
        s.IconAutoHideSeconds = IconSecondsSlider.Value;
        s.Hotkey = _hotkey;
        s.BlacklistProcesses = BlacklistBox.Text
            .Split(new[] { ',', '，', ';', '；', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        s.EnglishAccent = UkRadio.IsChecked == true ? "UK" : "US";
        s.SpeechRate = Math.Round(RateSlider.Value, 1);
        s.OfflineSpeechFallback = OfflineSwitch.IsChecked == true;
        s.Theme = ThemeBox.SelectedValue as string ?? "System";
        s.FontSize = FontSlider.Value;
        s.PopupWidth = WidthSlider.Value;
        s.AutoStart = AutoStartSwitch.IsChecked == true;
        s.FirstRunDone = true;

        if (LanguageInfo.SameLanguage(s.TargetLanguage, s.FallbackLanguage))
        {
            SaveHint.Text = "“译成”和“原文已是目标语言时译成”不能是同一种语言";
            return;
        }

        try
        {
            App.SettingsStore.Save(s);
        }
        catch (Exception ex)
        {
            SaveHint.Text = "保存失败：" + ex.Message;
            App.Log("保存设置失败", ex);
            return;
        }

        bool firstSetup = string.IsNullOrWhiteSpace(App.SettingsStore.Current.ApiKey) == false && WelcomeBar.IsOpen;
        Close();
        if (firstSetup)
            App.Notify("设置完成", $"现在选中任意文字，点击旁边的「译」图标即可翻译；也可以按 {s.Hotkey}。");
    }
}
