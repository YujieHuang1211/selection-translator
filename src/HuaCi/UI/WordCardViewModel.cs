using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HuaCi.Core;
using HuaCi.Translation;

namespace HuaCi.UI;

public sealed record SenseViewModel(int Index, string Pos, string Meaning, string Example, string ExampleTranslation, string Lang)
{
    public string Number => Index + ".";
}

/// <summary>One key-word card (or the full dictionary entry in word mode).</summary>
public sealed partial class WordCardViewModel : ObservableObject
{
    private const int CollapsedSenseCount = 1;
    private readonly string _sourceSentence;

    public WordCardViewModel(WordEntry entry, string sourceLang, string sourceSentence, bool isWordMode)
    {
        Entry = entry;
        SourceLang = sourceLang;
        _sourceSentence = sourceSentence;
        IsWordMode = isWordMode;
        isExpanded = isWordMode;
        AllSenses = entry.Senses
            .Select((s, i) => new SenseViewModel(i + 1, s.Pos, s.Meaning, s.Example, s.ExampleTranslation, sourceLang))
            .ToList();
        try { isStarred = App.History?.IsInVocab(VocabKey) ?? false; }
        catch { isStarred = false; }
    }

    public WordEntry Entry { get; }
    public string SourceLang { get; }
    public bool IsWordMode { get; }

    public string Word => Entry.Word;
    public string VocabKey => string.IsNullOrWhiteSpace(Entry.Lemma) ? Entry.Word : Entry.Lemma;

    public string Headword => Entry.Word;

    public string LemmaNote => !string.IsNullOrWhiteSpace(Entry.Lemma) && !Entry.Lemma.Equals(Entry.Word, StringComparison.OrdinalIgnoreCase)
        ? $"原形 {Entry.Lemma}"
        : "";

    public double HeadwordSize => IsWordMode ? 22 : 16;

    public bool IsEnglish => LanguageInfo.Find(SourceLang)?.Code == "en";

    public string PhoneticUs => IsEnglish ? Slashed(Entry.PhoneticUs) : Entry.PhoneticUs;
    public string PhoneticUk => IsEnglish ? Slashed(Entry.PhoneticUk) : "";
    public string UsLabel => IsEnglish ? "美" : "";
    public bool HasUs => !string.IsNullOrWhiteSpace(Entry.PhoneticUs) || !IsEnglish;
    public bool HasUk => IsEnglish && !string.IsNullOrWhiteSpace(Entry.PhoneticUk);

    public string Pos => Entry.Pos;
    public string Brief => Entry.Brief;
    public string ContextMeaning => Entry.ContextMeaning;
    public string Forms => Entry.Forms;
    public IReadOnlyList<string> Collocations => Entry.Collocations;

    public IReadOnlyList<SenseViewModel> AllSenses { get; }

    public IReadOnlyList<SenseViewModel> VisibleSenses => IsExpanded ? AllSenses : AllSenses.Take(CollapsedSenseCount).ToList();

    public bool CanExpand => !IsWordMode && (AllSenses.Count > CollapsedSenseCount || Collocations.Count > 0 || !string.IsNullOrWhiteSpace(Forms));

    public bool ShowExtras => IsExpanded;

    public string ExpandText => IsExpanded
        ? "收起"
        : AllSenses.Count > CollapsedSenseCount ? $"更多释义（{AllSenses.Count - CollapsedSenseCount}）" : "词形与搭配";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleSenses), nameof(ExpandText), nameof(ShowExtras))]
    private bool isExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StarTip))]
    private bool isStarred;

    public string StarTip => IsStarred ? "从生词本移除" : "加入生词本";

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void ToggleStar()
    {
        if (App.History == null) return;
        try
        {
            if (IsStarred) App.History.RemoveVocab(VocabKey);
            else App.History.AddVocab(Entry, SourceLang, _sourceSentence);
            IsStarred = !IsStarred;
        }
        catch (Exception ex)
        {
            App.Log("生词本操作失败", ex);
        }
    }

    private static string Slashed(string phonetic) => string.IsNullOrWhiteSpace(phonetic) ? "" : $"/{phonetic}/";
}
