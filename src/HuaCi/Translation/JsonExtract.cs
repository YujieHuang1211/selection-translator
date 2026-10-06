using System.Text.Json;

namespace HuaCi.Translation;

public static class JsonExtract
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Returns the outermost {...} object from a model reply, tolerating ```json fences and chatter.</summary>
    public static string? ExtractObject(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        int start = raw.IndexOf('{');
        int end = raw.LastIndexOf('}');
        return start >= 0 && end > start ? raw[start..(end + 1)] : null;
    }

    public static AnalysisResult ParseAnalysis(string raw)
    {
        var json = ExtractObject(raw) ?? throw new FormatException("模型没有返回有效的 JSON");
        var result = JsonSerializer.Deserialize<AnalysisResult>(json, Options)
                     ?? throw new FormatException("模型返回的 JSON 为空");
        result.Keywords ??= new();
        result.Keywords.RemoveAll(k => k is null || string.IsNullOrWhiteSpace(k.Word));
        foreach (var k in result.Keywords)
        {
            k.Senses ??= new();
            k.Collocations ??= new();
            k.Senses.RemoveAll(s => s is null || string.IsNullOrWhiteSpace(s.Meaning));
            k.PhoneticUs = TrimSlashes(k.PhoneticUs);
            k.PhoneticUk = TrimSlashes(k.PhoneticUk);
        }
        result.Mode = result.Mode == "word" ? "word" : "text";
        return result;
    }

    private static string TrimSlashes(string? s) => (s ?? "").Trim().Trim('/', '[', ']').Trim();
}
