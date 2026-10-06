using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using HuaCi.Data;
using HuaCi.Translation;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace HuaCi.UI;

public partial class HistoryWindow : FluentWindow
{
    private readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public HistoryWindow()
    {
        InitializeComponent();
        _searchDelay.Tick += (_, _) =>
        {
            _searchDelay.Stop();
            Reload();
        };
        Loaded += (_, _) => Reload();
        if (App.History != null) App.History.VocabChanged += OnVocabChanged;
        Closed += (_, _) =>
        {
            if (App.History != null) App.History.VocabChanged -= OnVocabChanged;
        };
    }

    private void OnVocabChanged() => Dispatcher.BeginInvoke(ReloadVocab);

    private void Reload()
    {
        ReloadHistory();
        ReloadVocab();
    }

    private void ReloadHistory()
    {
        var items = App.History?.SearchHistory(HistorySearch.Text) ?? new List<HistoryEntry>();
        HistoryList.ItemsSource = items;
        HistoryEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryEmpty.Text = string.IsNullOrWhiteSpace(HistorySearch.Text) ? "还没有翻译记录" : "没有找到匹配的记录";
    }

    private void ReloadVocab()
    {
        var items = App.History?.ListVocab(VocabSearch.Text) ?? new List<VocabEntry>();
        VocabList.ItemsSource = items;
        VocabEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        VocabCount.Text = items.Count > 0 ? $"共 {items.Count} 个单词" : "";
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == Tabs && IsLoaded) Reload();
    }

    private void HistorySearch_TextChanged(object sender, TextChangedEventArgs e) => _searchDelay.Start();

    private void VocabSearch_TextChanged(object sender, TextChangedEventArgs e) => _searchDelay.Start();

    private void HistoryItem_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not HistoryEntry h || App.Controller == null) return;
        AnalysisResult? analysis = null;
        if (!string.IsNullOrEmpty(h.AnalysisJson))
        {
            try { analysis = JsonExtract.ParseAnalysis(h.AnalysisJson); }
            catch (Exception ex) { App.Log("解析历史记录失败", ex); }
        }
        App.Controller.ShowCached(h.Text, h.Translation, analysis);
    }

    private void DeleteHistory_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not HistoryEntry h) return;
        App.History?.DeleteHistory(h.Id);
        ReloadHistory();
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(this, "确定清空所有翻译历史吗？生词本不受影响。", "清空历史",
            System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question);
        if (result != System.Windows.MessageBoxResult.OK) return;
        App.History?.ClearHistory();
        ReloadHistory();
    }

    private void DeleteVocab_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is VocabEntry v) App.History?.RemoveVocab(v.Word);
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e) =>
        Export("CSV 文件|*.csv", $"生词本_{DateTime.Now:yyyyMMdd}.csv", path => App.History!.ExportVocabCsv(path));

    private void ExportAnki_Click(object sender, RoutedEventArgs e) =>
        Export("Anki 导入文件|*.txt", $"生词本_Anki_{DateTime.Now:yyyyMMdd}.txt", path => App.History!.ExportVocabAnki(path));

    private void Export(string filter, string fileName, Action<string> write)
    {
        if (App.History == null) return;
        var dialog = new SaveFileDialog { Filter = filter, FileName = fileName };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            write(dialog.FileName);
            VocabCount.Text = "已导出到 " + dialog.FileName;
        }
        catch (Exception ex)
        {
            VocabCount.Text = "导出失败：" + ex.Message;
            App.Log("导出生词本失败", ex);
        }
    }
}
