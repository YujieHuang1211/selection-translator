using System.Text.Json;
using HuaCi.Data;

namespace HuaCi.Translation;

public sealed record CachedTranslation(string Translation, AnalysisResult? Analysis);

/// <summary>Runs the two parallel model calls (streamed translation + structured analysis) and caches results.</summary>
public sealed class TranslationService
{
    private readonly OpenRouterClient _client;
    private readonly HistoryStore? _history;

    public TranslationService(OpenRouterClient client, HistoryStore? history)
    {
        _client = client;
        _history = history;
    }

    public IAsyncEnumerable<string> TranslateStreamAsync(TranslationRequest r, CancellationToken ct) =>
        _client.StreamChatAsync(r.Model, Prompts.TranslationSystem(r), r.Text, 0.3, ct);

    public async Task<AnalysisResult> AnalyzeAsync(TranslationRequest r, CancellationToken ct)
    {
        var raw = await _client.CompleteJsonAsync(r.Model, Prompts.AnalysisSystem(r), r.Text, Prompts.AnalysisSchema(), ct);
        return JsonExtract.ParseAnalysis(raw);
    }

    public CachedTranslation? TryGetCached(TranslationRequest r)
    {
        var row = _history?.FindByCacheKey(r.CacheKey);
        if (row == null || string.IsNullOrEmpty(row.Translation)) return null;
        AnalysisResult? analysis = null;
        if (!string.IsNullOrEmpty(row.AnalysisJson))
        {
            try { analysis = JsonExtract.ParseAnalysis(row.AnalysisJson); }
            catch (FormatException) { }
            catch (JsonException) { }
        }
        return new CachedTranslation(row.Translation, analysis);
    }

    public void Remember(TranslationRequest r, string translation, AnalysisResult? analysis)
    {
        if (_history == null || string.IsNullOrWhiteSpace(translation)) return;
        try
        {
            _history.AddHistory(new HistoryEntry
            {
                Text = r.Text,
                Translation = translation,
                AnalysisJson = analysis == null ? "" : JsonSerializer.Serialize(analysis),
                SourceLang = analysis?.SourceLang ?? "",
                TargetLang = r.TargetLanguage,
                Model = r.Model,
                CacheKey = r.CacheKey,
                CreatedAt = DateTime.Now,
            });
        }
        catch (Exception ex)
        {
            App.Log("保存历史失败", ex);
        }
    }
}
