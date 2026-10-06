using System.Text.Json.Serialization;

namespace HuaCi.Translation;

public sealed class AnalysisResult
{
    [JsonPropertyName("source_lang")] public string SourceLang { get; set; } = "";

    /// <summary>"word" = dictionary entry for a single word/phrase; "text" = key words from a sentence.</summary>
    [JsonPropertyName("mode")] public string Mode { get; set; } = "text";

    [JsonPropertyName("keywords")] public List<WordEntry> Keywords { get; set; } = new();

    [JsonIgnore] public bool IsWordMode => Mode == "word";
}

public sealed class WordEntry
{
    [JsonPropertyName("word")] public string Word { get; set; } = "";
    [JsonPropertyName("lemma")] public string Lemma { get; set; } = "";
    [JsonPropertyName("phonetic_us")] public string PhoneticUs { get; set; } = "";
    [JsonPropertyName("phonetic_uk")] public string PhoneticUk { get; set; } = "";
    [JsonPropertyName("pos")] public string Pos { get; set; } = "";
    [JsonPropertyName("brief")] public string Brief { get; set; } = "";
    [JsonPropertyName("context_meaning")] public string ContextMeaning { get; set; } = "";
    [JsonPropertyName("senses")] public List<Sense> Senses { get; set; } = new();
    [JsonPropertyName("forms")] public string Forms { get; set; } = "";
    [JsonPropertyName("collocations")] public List<string> Collocations { get; set; } = new();
}

public sealed class Sense
{
    [JsonPropertyName("pos")] public string Pos { get; set; } = "";
    [JsonPropertyName("meaning")] public string Meaning { get; set; } = "";
    [JsonPropertyName("example")] public string Example { get; set; } = "";
    [JsonPropertyName("example_translation")] public string ExampleTranslation { get; set; } = "";
}

public sealed record ModelInfo(string Id, string Name, decimal PromptPricePerM, decimal CompletionPricePerM, long ContextLength)
{
    public string PriceText => PromptPricePerM == 0 && CompletionPricePerM == 0
        ? "免费"
        : $"输入 ${PromptPricePerM:0.###}/M · 输出 ${CompletionPricePerM:0.###}/M";
}

public sealed record TranslationRequest(
    string Text,
    string TargetLanguage,
    string FallbackLanguage,
    string ExplainLanguage,
    string VocabLevel,
    string Model)
{
    public string CacheKey => string.Join('\u001F', Text, TargetLanguage, FallbackLanguage, ExplainLanguage, VocabLevel, Model, Prompts.Version);
}
