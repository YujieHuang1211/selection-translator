using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HuaCi.Translation;
using HuaCi.UI;

namespace HuaCi;

/// <summary>
/// Developer aid: <c>HuaCi.exe --snapshot &lt;dir&gt; [light|dark]</c> renders the main windows off-screen to PNG files,
/// so layouts can be checked without touching the user's desktop, mouse or focus.
/// </summary>
internal static class UiSnapshot
{
    public static async Task<int> RunAsync(string[] args)
    {
        var i = Array.IndexOf(args, "--snapshot");
        var dir = i + 1 < args.Length ? args[i + 1] : Path.GetTempPath();
        var theme = args.Contains("dark") ? "Dark" : "Light";
        Directory.CreateDirectory(dir);
        App.ApplyTheme(theme);

        var analysis = JsonExtract.ParseAnalysis("""
            {"source_lang":"en","mode":"text","keywords":[
             {"word":"skepticism","lemma":"skepticism","phonetic_us":"ˈskeptɪsɪzəm","phonetic_uk":"ˈskeptɪsɪzəm","pos":"n.","brief":"怀疑态度；怀疑论",
              "context_meaning":"指专家们对委员会决定持怀疑态度。",
              "senses":[{"pos":"n.","meaning":"怀疑态度","example":"The claims were met with skepticism.","example_translation":"这些说法遭到了质疑。"},
                        {"pos":"n.","meaning":"怀疑论（哲学）","example":"Philosophical skepticism questions knowledge.","example_translation":"哲学怀疑论质疑知识的可能性。"}],
              "forms":"形容词 skeptical","collocations":["healthy skepticism","widespread skepticism"]},
             {"word":"stifle","lemma":"stifle","phonetic_us":"ˈstaɪfl","phonetic_uk":"ˈstaɪfl","pos":"v.","brief":"扼杀；抑制",
              "context_meaning":"指法规会压制创新。","senses":[{"pos":"v.","meaning":"扼杀，压制","example":"Rules can stifle creativity.","example_translation":"规则可能扼杀创造力。"}],
              "forms":"","collocations":[]}]}
            """);

        var popup = new TranslatePopup { Left = -20000, Top = -20000 };
        popup.ViewModel.ShowCached(
            "The committee's decision was met with considerable skepticism, as many experts argued that the proposed regulations would inadvertently stifle innovation.",
            "委员会的决定遭到了相当多的质疑，因为许多专家认为，拟议的法规会在无意中扼杀创新。",
            analysis);
        await Render(popup, Path.Combine(dir, $"popup-{theme}.png"));

        var settings = new SettingsWindow { Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual };
        await Render(settings, Path.Combine(dir, $"settings-{theme}.png"));

        var history = new HistoryWindow { Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual };
        await Render(history, Path.Combine(dir, $"history-{theme}.png"));
        return 0;
    }

    private static async Task Render(Window w, string path)
    {
        w.ShowActivated = false;
        w.ShowInTaskbar = false;
        w.Show();
        await Task.Delay(600); // 让布局、绑定和模型列表等异步内容就位
        w.UpdateLayout();
        var content = (FrameworkElement)w.Content;
        double scale = VisualTreeHelper.GetDpi(w).DpiScaleX;
        var bmp = new RenderTargetBitmap(
            (int)Math.Ceiling(content.ActualWidth * scale), (int)Math.Ceiling(content.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        // 白/黑底，避免透明区域看不清
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen())
            dc.DrawRectangle(App.IsDarkTheme ? Brushes.Black : Brushes.White, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        bmp.Render(bg);
        bmp.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        await using (var fs = File.Create(path)) encoder.Save(fs);
        w.Close();
    }
}
