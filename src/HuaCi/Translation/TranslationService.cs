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

    /// <summary>
    /// Streams the key-word analysis. <paramref name="onKeywords"/> is called (on the caller's context) with the
    /// source language / mode known so far and each newly completed key word, before the whole result is done.
    /// </summary>
    public async Task<AnalysisResult> AnalyzeAsync(
        TranslationRequest r, CancellationToken ct,
        Action<string?, string?, IReadOnlyList<WordEntry>>? onKeywords = null)
    {
        var parser = new IncrementalAnalysisParser();
        string? lastMode = null;
        await foreach (var chunk in _client.StreamJsonAsync(r.Model, Prompts.AnalysisSystem(r), r.Text, Prompts.AnalysisSchema(), ct))
        {
            var fresh = parser.Feed(chunk);
            if (fresh.Count > 0 || parser.Mode != lastMode)
            {
                lastMode = parser.Mode;
                onKeywords?.Invoke(parser.SourceLang, parser.Mode, fresh);
            }
        }
        return JsonExtract.ParseAnalysis(parser.Text);
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
