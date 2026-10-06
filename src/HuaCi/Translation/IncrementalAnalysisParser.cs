using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HuaCi.Translation;

/// <summary>
/// Parses the analysis JSON while it is still streaming, so each key-word card can be shown
/// as soon as its object in the "keywords" array is complete.
/// </summary>
public sealed partial class IncrementalAnalysisParser
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    private readonly StringBuilder _buffer = new();
    private int _emitted;

    [GeneratedRegex("\"source_lang\"\\s*:\\s*\"([^\"]*)\"")]
    private static partial Regex SourceLangRegex();

    [GeneratedRegex("\"mode\"\\s*:\\s*\"(word|text)\"")]
    private static partial Regex ModeRegex();

    public string? SourceLang { get; private set; }
    public string? Mode { get; private set; }
    public string Text => _buffer.ToString();

    /// <summary>Appends a chunk and returns the key words that became complete with it.</summary>
    public List<WordEntry> Feed(string chunk)
    {
        _buffer.Append(chunk);
        var text = _buffer.ToString();
        if (SourceLang == null && SourceLangRegex().Match(text) is { Success: true } sl) SourceLang = sl.Groups[1].Value;
        if (Mode == null && ModeRegex().Match(text) is { Success: true } m) Mode = m.Groups[1].Value;

        var objects = CompleteKeywordObjects(text);
        var result = new List<WordEntry>();
        for (int i = _emitted; i < objects.Count; i++)
        {
            try
            {
                var entry = JsonSerializer.Deserialize<WordEntry>(objects[i], Options);
                if (entry != null && !string.IsNullOrWhiteSpace(entry.Word)) result.Add(JsonExtract.Clean(entry));
            }
            catch (JsonException)
            {
                // 单个对象坏了就跳过，最终完整解析时还有机会
            }
        }
        _emitted = objects.Count;
        return result;
    }

    /// <summary>Returns the complete top-level objects inside the "keywords" array (string-aware brace matching).</summary>
    internal static List<string> CompleteKeywordObjects(string text)
    {
        var list = new List<string>();
        int key = text.IndexOf("\"keywords\"", StringComparison.Ordinal);
        if (key < 0) return list;
        int i = text.IndexOf('[', key);
        if (i < 0) return list;

        bool inString = false, escape = false;
        int depth = 0, start = -1;
        for (i++; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '{':
                    if (depth == 0) start = i;
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        list.Add(text[start..(i + 1)]);
                        start = -1;
                    }
                    break;
                case ']':
                    if (depth == 0) return list;
                    break;
            }
        }
        return list;
    }
}
