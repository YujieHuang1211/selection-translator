using System.IO;
using System.Text;
using System.Text.Json;
using HuaCi.Core;
using HuaCi.Speech;
using HuaCi.Translation;

namespace HuaCi;

/// <summary>
/// Headless end-to-end check: <c>HuaCi.exe --selftest "text" [--out report.txt] [--save-key]</c>.
/// Uses OPENROUTER_API_KEY or the saved key, calls translation + analysis + both TTS engines.
/// </summary>
internal static class SelfTest
{
    public static async Task<int> RunAsync(string[] args)
    {
        var outPath = ArgAfter(args, "--out");
        var report = new StringBuilder();
        void W(string line)
        {
            report.AppendLine(line);
            Console.WriteLine(line);
        }

        int failures = 0;
        var settings = App.SettingsStore.Current;
        var envKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
        {
            settings.ApiKey = envKey.Trim();
            if (args.Contains("--save-key"))
            {
                App.SettingsStore.Save(settings);
                W("API Key 已加密保存到设置中");
            }
        }

        var text = ArgAfter(args, "--selftest");
        if (string.IsNullOrWhiteSpace(text) || text.StartsWith("--")) text = "Ubiquitous computing is reshaping how we live and work.";
        text = TextNormalizer.Normalize(text);
        W($"模型：{settings.Model}");
        W($"原文：{text}");

        var request = new TranslationRequest(text, settings.TargetLanguage, settings.FallbackLanguage, settings.ExplainLanguage, settings.VocabLevel, settings.Model);
        var service = new TranslationService(App.OpenRouter, history: null);

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long firstTokenMs = -1;
            var sb = new StringBuilder();
            var analysisTask = Task.Run(async () =>
            {
                var t = System.Diagnostics.Stopwatch.StartNew();
                var r = await service.AnalyzeAsync(request, CancellationToken.None);
                return (r, t.ElapsedMilliseconds);
            });
            await foreach (var part in service.TranslateStreamAsync(request, CancellationToken.None))
            {
                if (firstTokenMs < 0) firstTokenMs = sw.ElapsedMilliseconds;
                sb.Append(part);
            }
            W($"译文：{sb.ToString().Trim()}");
            W($"  首字 {firstTokenMs} ms，完成 {sw.ElapsedMilliseconds} ms");

            var (analysis, analysisMs) = await analysisTask;
            W($"解析（{analysisMs} ms）：mode={analysis.Mode}, source_lang={analysis.SourceLang}, 词数={analysis.Keywords.Count}");
            W(JsonSerializer.Serialize(analysis, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            if (sb.Length == 0) failures++;
        }
        catch (Exception ex)
        {
            failures++;
            W("翻译失败：" + ex.Message);
        }

        var audioDir = Path.Combine(Path.GetTempPath(), "huaci-selftest");
        Directory.CreateDirectory(audioDir);
        var lang = LanguageInfo.DetectScript(text);
        try
        {
            var mp3 = await new EdgeTtsClient().SynthesizeAsync(text, LanguageInfo.VoiceFor(lang), 1.0, CancellationToken.None);
            var path = Path.Combine(audioDir, "edge.mp3");
            await File.WriteAllBytesAsync(path, mp3);
            W($"Edge 在线语音：{mp3.Length} 字节 → {path}");
            if (mp3.Length < 1000) failures++;
        }
        catch (Exception ex)
        {
            failures++;
            W("Edge 在线语音失败：" + ex.Message);
        }

        try
        {
            var wav = await WindowsTts.SynthesizeAsync(text, "en-US", 1.0, CancellationToken.None);
            var path = Path.Combine(audioDir, "offline.wav");
            await File.WriteAllBytesAsync(path, wav);
            W($"Windows 离线语音：{wav.Length} 字节 → {path}");
            if (wav.Length < 1000) failures++;
        }
        catch (Exception ex)
        {
            failures++;
            W("Windows 离线语音失败：" + ex.Message);
        }

        W(failures == 0 ? "自检通过" : $"自检有 {failures} 项失败");
        if (outPath != null) await File.WriteAllTextAsync(outPath, report.ToString(), new UTF8Encoding(false));
        return failures == 0 ? 0 : 1;
    }

    private static string? ArgAfter(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
