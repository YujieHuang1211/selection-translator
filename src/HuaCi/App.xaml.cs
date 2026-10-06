using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using HuaCi.Core;
using HuaCi.Data;
using HuaCi.Selection;
using HuaCi.Speech;
using HuaCi.Translation;
using HuaCi.UI;
using Microsoft.Win32;
using Wpf.Ui.Appearance;

namespace HuaCi;

public partial class App : Application
{
    private const string MutexName = "HuaCi.SingleInstance.7F3A2C";
    private const string ActivateEventName = "HuaCi.Activate.7F3A2C";
    private static readonly object LogLock = new();

    private Mutex? _mutex;
    private EventWaitHandle? _activateEvent;
    private TaskbarIcon? _tray;
    private MenuItem? _pauseItem;
    private static SettingsWindow? _settingsWindow;
    private static HistoryWindow? _historyWindow;

    public static string DataDirectory { get; private set; } = SettingsStore.DefaultDataDirectory;
    public static SettingsStore SettingsStore { get; private set; } = null!;
    public static HistoryStore? History { get; private set; }
    public static OpenRouterClient OpenRouter { get; private set; } = null!;
    public static TranslationService Translator { get; private set; } = null!;
    public static SpeechService? Speech { get; private set; }
    public static TextGrabber Grabber { get; private set; } = null!;
    public static SelectionController? Controller { get; private set; }

    public static new App Current => (App)Application.Current;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        RegisterGlobalExceptionHandlers();

        if (e.Args.Contains("--selftest"))
        {
            InitServices();
            var code = await SelfTest.RunAsync(e.Args);
            Shutdown(code);
            return;
        }

        if (e.Args.Contains("--snapshot"))
        {
            InitServices();
            Speech = new SpeechService(() => SettingsStore.Current);
            var code = await UiSnapshot.RunAsync(e.Args);
            Shutdown(code);
            return;
        }

        _mutex = new Mutex(true, MutexName, out bool isFirst);
        if (!isFirst)
        {
            // 已经在运行：通知那个实例打开设置窗口
            try { EventWaitHandle.OpenExisting(ActivateEventName).Set(); } catch { /* ignore */ }
            Shutdown();
            return;
        }
        ListenForSecondInstance();

        InitServices();
        ApplyTheme(SettingsStore.Current.Theme);
        SystemEvents.UserPreferenceChanged += (_, args) =>
        {
            if (args.Category == UserPreferenceCategory.General && SettingsStore.Current.Theme == "System")
                Dispatcher.BeginInvoke(() => ApplyTheme("System"));
        };

        Speech = new SpeechService(() => SettingsStore.Current);
        Speech.FellBackToOffline += reason =>
            Notify("在线语音暂时不可用", "已自动改用 Windows 离线语音朗读。");

        Controller = new SelectionController();
        string? hotkeyMessage = null;
        try
        {
            hotkeyMessage = Controller.Start();
            PersistActiveHotkey();
        }
        catch (Exception ex)
        {
            Log("启动划词监听失败", ex);
            MessageBox.Show("无法启动全局划词监听：" + ex.Message, "HuaCi 划词翻译", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        Controller.PausedChanged += _ => UpdateTray();

        CreateTray();
        OpenRouterClient.Warmup();
        SettingsStore.Changed += OnSettingsChanged;

        var s = SettingsStore.Current;
        if (string.IsNullOrWhiteSpace(s.ApiKey))
        {
            ShowSettings();
        }
        else if (hotkeyMessage != null)
        {
            Notify("快捷键提示", hotkeyMessage);
        }
        else if (!e.Args.Contains("--background"))
        {
            var hotkeyTip = string.IsNullOrEmpty(s.Hotkey) ? "" : $"，或按 {s.Hotkey} 直接翻译";
            Notify("划词翻译已在后台运行", $"选中文字后点击旁边的「译」图标即可翻译{hotkeyTip}。右键托盘图标可打开设置。");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Controller?.Dispose();
        Speech?.Dispose();
        _tray?.Dispose();
        _activateEvent?.Dispose();
        if (_mutex != null)
        {
            try { _mutex.ReleaseMutex(); } catch { /* not owned */ }
            _mutex.Dispose();
        }
        base.OnExit(e);
    }

    private static void InitServices()
    {
        SettingsStore = new SettingsStore(DataDirectory);
        SettingsStore.Load();
        try
        {
            History = new HistoryStore(Path.Combine(DataDirectory, "huaci.db"));
        }
        catch (Exception ex)
        {
            Log("打开历史数据库失败，历史与缓存将不可用", ex);
        }
        OpenRouter = new OpenRouterClient(() => SettingsStore.Current.ApiKey);
        Translator = new TranslationService(OpenRouter, History);
        Grabber = new TextGrabber(() => SettingsStore.Current);
    }

    private void OnSettingsChanged(AppSettings s)
    {
        Dispatcher.BeginInvoke(() =>
        {
            ApplyTheme(s.Theme);
            var hotkeyMessage = Controller?.ApplySettings(s);
            if (hotkeyMessage != null) Notify("快捷键提示", hotkeyMessage);
            PersistActiveHotkey();
            try
            {
                if (AutoStart.IsEnabled() != s.AutoStart) AutoStart.Set(s.AutoStart);
            }
            catch (Exception ex)
            {
                Log("设置开机自启失败", ex);
            }
        });
    }

    /// <summary>After an automatic hotkey fallback, store the key that actually works so Settings shows it.</summary>
    private static void PersistActiveHotkey()
    {
        var active = Controller?.ActiveHotkey;
        var s = SettingsStore.Current;
        if (active == null || string.Equals(active, s.Hotkey, StringComparison.OrdinalIgnoreCase)) return;
        if (Hotkey.TryParse(s.Hotkey, out var configured) && configured.ToString() == active) return;
        var updated = s.Clone();
        updated.Hotkey = active;
        SettingsStore.Save(updated);
    }

    // ---------------- windows ----------------

    public static void ShowSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow();
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    public static void ShowHistory()
    {
        if (_historyWindow is { IsLoaded: true })
        {
            _historyWindow.Activate();
            return;
        }
        _historyWindow = new HistoryWindow();
        _historyWindow.Closed += (_, _) => _historyWindow = null;
        _historyWindow.Show();
        _historyWindow.Activate();
    }

    // ---------------- tray ----------------

    private void CreateTray()
    {
        _pauseItem = new MenuItem { Header = "暂停划词", IsCheckable = true };
        _pauseItem.Click += (_, _) =>
        {
            if (Controller != null) Controller.Paused = _pauseItem.IsChecked;
        };

        var openItem = new MenuItem { Header = "打开翻译窗口" };
        openItem.Click += (_, _) => OpenInputPopup();
        var historyItem = new MenuItem { Header = "历史记录与生词本" };
        historyItem.Click += (_, _) => ShowHistory();
        var settingsItem = new MenuItem { Header = "设置" };
        settingsItem.Click += (_, _) => ShowSettings();
        var exitItem = new MenuItem { Header = "退出" };
        exitItem.Click += (_, _) => Shutdown();

        var menu = new ContextMenu();
        menu.Items.Add(openItem);
        menu.Items.Add(historyItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);

        _tray = new TaskbarIcon
        {
            ToolTipText = "HuaCi 划词翻译",
            IconSource = LoadIcon("app.ico"),
            ContextMenu = menu,
            NoLeftClickDelay = true,
            MenuActivation = PopupActivationMode.RightClick,
        };
        _tray.TrayLeftMouseUp += (_, _) => OpenInputPopup();
        _tray.ForceCreate(enablesEfficiencyMode: false);
    }

    private void OpenInputPopup()
    {
        if (Controller == null) return;
        Selection.NativeMethods.GetCursorPos(out var p);
        var popup = Controller.Popup;
        popup.ShowAt(p);
        popup.ViewModel.ShowEmpty("在上面输入或粘贴要翻译的文字，按 Enter 翻译");
        popup.FocusSourceInput();
    }

    private void UpdateTray()
    {
        if (_tray == null || Controller == null) return;
        bool paused = Controller.Paused;
        if (_pauseItem != null) _pauseItem.IsChecked = paused;
        _tray.IconSource = LoadIcon(paused ? "app-paused.ico" : "app.ico");
        _tray.ToolTipText = paused ? "HuaCi 划词翻译（已暂停）" : "HuaCi 划词翻译";
    }

    private static ImageSource LoadIcon(string name) =>
        new BitmapImage(new Uri($"pack://application:,,,/Assets/{name}", UriKind.Absolute));

    public static void Notify(string title, string message)
    {
        try
        {
            Current._tray?.ShowNotification(title, message, NotificationIcon.Info);
        }
        catch (Exception ex)
        {
            Log("托盘通知失败", ex);
        }
    }

    // ---------------- theme ----------------

    public static bool IsDarkTheme { get; private set; }

    public static void ApplyTheme(string theme)
    {
        bool dark = theme switch
        {
            "Dark" => true,
            "Light" => false,
            _ => SystemUsesDarkTheme(),
        };
        IsDarkTheme = dark;
        ApplicationThemeManager.Apply(dark ? ApplicationTheme.Dark : ApplicationTheme.Light, updateAccent: false);
        var res = Application.Current.Resources;
        res["HcAccentTextBrush"] = Brush(dark ? "#A5B4FC" : "#4F46E5");
        res["HcAccentSoftBrush"] = Brush(dark ? "#336366F1" : "#1A6366F1");
    }

    private static SolidColorBrush Brush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    private static bool SystemUsesDarkTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }

    // ---------------- infrastructure ----------------

    private void ListenForSecondInstance()
    {
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        var thread = new Thread(() =>
        {
            while (_activateEvent.WaitOne())
                Dispatcher.BeginInvoke(ShowSettings);
        })
        { IsBackground = true, Name = "HuaCi activate listener" };
        thread.Start();
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log("未处理的界面异常", args.Exception);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log("未观察到的任务异常", args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log("未处理的异常", args.ExceptionObject as Exception);
    }

    public static void Log(string message, Exception? ex = null)
    {
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(DataDirectory);
                var path = Path.Combine(DataDirectory, "log.txt");
                if (File.Exists(path) && new FileInfo(path).Length > 1_000_000)
                    File.Move(path, path + ".old", overwrite: true);
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{(ex == null ? "" : ": " + ex)}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志失败不能影响使用
        }
    }
}
