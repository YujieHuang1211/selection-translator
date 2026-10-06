using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HuaCi.Core;
using HuaCi.Translation;

namespace HuaCi.UI;

public sealed partial class TranslatePopupViewModel : ObservableObject
{
    private CancellationTokenSource? _cts;
    private int _run;

    public IReadOnlyList<LanguageInfo> Languages => LanguageInfo.All;

    public ObservableCollection<WordCardViewModel> Keywords { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSourceLong), nameof(HasSource))]
    private string sourceText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceLangName), nameof(EffectiveTarget), nameof(EffectiveTargetName))]
    private string sourceLang = "en";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTranslation), nameof(ShowTranslationSkeleton))]
    private string translation = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTranslationSkeleton))]
    private bool isTranslating;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyKeywords), nameof(KeywordsTitle))]
    private bool isAnalyzing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeywordsTitle))]
    private bool isWordMode;

    [ObservableProperty]
    private string? errorMessage;

    [ObservableProperty]
    private bool errorIsAuth;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyKeywords))]
    private string? analysisError;

    [ObservableProperty]
    private bool isPinned;

    [ObservableProperty]
    private bool isSourceExpanded;

    [ObservableProperty]
    private string? notice;

    [ObservableProperty]
    private bool copied;

    public bool HasSource => !string.IsNullOrWhiteSpace(SourceText);
    public bool IsSourceLong => SourceText.Length > 180 || SourceText.Count(c => c == '\n') >= 3;
    public bool HasTranslation => !string.IsNullOrEmpty(Translation);
    public bool ShowTranslationSkeleton => IsTranslating && !HasTranslation;
    public bool ShowEmptyKeywords => !IsAnalyzing && AnalysisError == null && Keywords.Count == 0 && HasTranslation;
    public string KeywordsTitle => IsWordMode ? "词典释义" : "重点词汇";

    public string SourceLangName => LanguageInfo.NameOf(SourceLang);

    private static AppSettings Settings => App.SettingsStore.Current;

    /// <summary>The language we actually translate into: target, or fallback when the text is already in the target language.</summary>
    public string EffectiveTarget =>
        LanguageInfo.SameLanguage(SourceLang, Settings.TargetLanguage) ? Settings.FallbackLanguage : Settings.TargetLanguage;

    public string EffectiveTargetName => LanguageInfo.NameOf(EffectiveTarget);

    public double FontSize => Settings.FontSize;

    /// <summary>Raised when the user should be taken to Settings (e.g. missing API key).</summary>
    public event Action? OpenSettingsRequested;

    public async Task StartAsync(string rawText)
    {
        var normalized = TextNormalizer.Normalize(rawText);
        var (text, truncated) = TextNormalizer.Limit(normalized);
        SourceText = text;
        IsSourceExpanded = false;
        Notice = truncated ? $"文字过长，只翻译了前 {TextNormalizer.MaxLength} 个字符" : null;
        await RunAsync();
    }

    /// <summary>Shows an empty popup where the user can type text to translate.</summary>
    public void ShowEmpty(string notice)
    {
        Cancel();
        SourceText = "";
        Translation = "";
        Keywords.Clear();
        IsTranslating = IsAnalyzing = false;
        ErrorMessage = AnalysisError = null;
        Notice = notice;
        OnPropertyChanged(nameof(ShowEmptyKeywords));
    }

    public void ShowCached(string text, string translationText, AnalysisResult? analysis)
    {
        Cancel();
        SourceText = text;
        SourceLang = analysis?.SourceLang is { Length: > 0 } l ? l : LanguageInfo.DetectScript(text);
        Translation = translationText;
        IsTranslating = false;
        ErrorMessage = AnalysisError = null;
        Notice = null;
        ApplyAnalysis(analysis);
    }

    [RelayCommand]
    private Task Retry() => RunAsync();

    [RelayCommand]
    private void OpenSettings() => OpenSettingsRequested?.Invoke();

    [RelayCommand]
    private async Task CopyTranslation()
    {
        if (!HasTranslation) return;
        try
        {
            System.Windows.Clipboard.SetText(Translation);
            Copied = true;
            await Task.Delay(1200);
            Copied = false;
        }
        catch (Exception ex)
        {
            App.Log("复制失败", ex);
        }
    }

    [RelayCommand]
    private void ToggleSource() => IsSourceExpanded = !IsSourceExpanded;

    /// <summary>Called when the user picks another target language in the header.</summary>
    public async Task ChangeTargetAsync(string code)
    {
        if (LanguageInfo.SameLanguage(code, EffectiveTarget)) return;
        var s = Settings.Clone();
        // 当前是“原文=目标语言 → 走备选语言”的情况时，改的是备选语言，否则改目标语言
        if (LanguageInfo.SameLanguage(SourceLang, s.TargetLanguage)) s.FallbackLanguage = code;
        else s.TargetLanguage = code;
        App.SettingsStore.Save(s);
        OnPropertyChanged(nameof(EffectiveTarget));
        OnPropertyChanged(nameof(EffectiveTargetName));
        if (HasSource) await RunAsync();
    }

    public void Cancel()
    {
        _cts?.Cancel();
        _cts = null;
        _run++;
    }

    private async Task RunAsync()
    {
        Cancel();
        var run = _run;
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;

        Translation = "";
        Keywords.Clear();
        ErrorMessage = null;
        ErrorIsAuth = false;
        AnalysisError = null;
        IsWordMode = TextNormalizer.LooksLikeWord(SourceText);
        if (!HasSource)
        {
            IsTranslating = IsAnalyzing = false;
            return;
        }

        SourceLang = LanguageInfo.DetectScript(SourceText);
        var s = Settings;
        var request = new TranslationRequest(SourceText, s.TargetLanguage, s.FallbackLanguage, s.ExplainLanguage, s.VocabLevel, s.Model);

        var cached = App.Translator.TryGetCached(request);
        if (cached != null)
        {
            Translation = cached.Translation;
            ApplyAnalysis(cached.Analysis);
            IsTranslating = IsAnalyzing = false;
            return;
        }

        IsTranslating = true;
        IsAnalyzing = true;

        var translateTask = StreamTranslationAsync(request, run, ct);
        var analysisTask = AnalyzeAsync(request, run, ct);
        await Task.WhenAll(translateTask, analysisTask);

        if (run == _run && !ct.IsCancellationRequested && ErrorMessage == null)
            App.Translator.Remember(request, Translation, await analysisTask);
    }

    private async Task StreamTranslationAsync(TranslationRequest request, int run, CancellationToken ct)
    {
        var sb = new StringBuilder();
        try
        {
            await foreach (var part in App.Translator.TranslateStreamAsync(request, ct))
            {
                if (run != _run) return;
                sb.Append(part);
                Translation = sb.ToString().TrimStart();
            }
            if (run == _run) Translation = sb.ToString().Trim();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (run != _run) return;
            ErrorIsAuth = ex is OpenRouterException { IsAuthProblem: true };
            ErrorMessage = ex is OpenRouterException ? ex.Message : "翻译失败：" + ex.Message;
            App.Log("翻译失败", ex);
        }
        finally
        {
            if (run == _run) IsTranslating = false;
            OnPropertyChanged(nameof(ShowEmptyKeywords));
        }
    }

    private async Task<AnalysisResult?> AnalyzeAsync(TranslationRequest request, int run, CancellationToken ct)
    {
        try
        {
            var result = await App.Translator.AnalyzeAsync(request, ct);
            if (run != _run) return null;
            ApplyAnalysis(result);
            return result;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            if (run != _run) return null;
            // 鉴权类错误已在译文区提示，这里不再重复
            AnalysisError = ex is OpenRouterException { IsAuthProblem: true } ? null : "词汇解析失败";
            App.Log("词汇解析失败", ex);
            return null;
        }
        finally
        {
            if (run == _run) IsAnalyzing = false;
        }
    }

    private void ApplyAnalysis(AnalysisResult? analysis)
    {
        Keywords.Clear();
        IsAnalyzing = false;
        if (analysis == null)
        {
            OnPropertyChanged(nameof(ShowEmptyKeywords));
            return;
        }
        if (!string.IsNullOrWhiteSpace(analysis.SourceLang)) SourceLang = analysis.SourceLang;
        IsWordMode = analysis.IsWordMode;
        foreach (var k in analysis.Keywords)
            Keywords.Add(new WordCardViewModel(k, SourceLang, SourceText, analysis.IsWordMode));
        OnPropertyChanged(nameof(ShowEmptyKeywords));
    }
}
