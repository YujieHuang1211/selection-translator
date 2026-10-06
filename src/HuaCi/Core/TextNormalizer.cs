using System.Text;
using System.Text.RegularExpressions;

namespace HuaCi.Core;

/// <summary>Cleans text copied from PDFs/web pages before it is shown and translated.</summary>
public static partial class TextNormalizer
{
    public const int MaxLength = 5000;

    [GeneratedRegex(@"(\p{L})-\n(\p{Ll})")]
    private static partial Regex HyphenBreak();

    [GeneratedRegex(@"\n[ \t]*\n\s*")]
    private static partial Regex ParagraphBreak();

    [GeneratedRegex(@"^\s*([•·▪◦*\-–—]|\d{1,3}[.)、]|[a-zA-Z][.)]|[（(]\d{1,3}[)）])\s")]
    private static partial Regex ListItemStart();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex MultiSpace();

    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var text = raw.Replace("\r\n", "\n").Replace('\r', '\n')
            .Replace("­", "")      // soft hyphen
            .Replace(' ', ' ')     // nbsp
            .Replace('\t', ' ')
            .Trim();

        text = HyphenBreak().Replace(text, "$1$2");

        var paragraphs = ParagraphBreak().Split(text);
        var sb = new StringBuilder(text.Length);
        foreach (var para in paragraphs)
        {
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(JoinWrappedLines(para));
        }

        return MultiSpace().Replace(sb.ToString(), " ").Trim();
    }

    /// <summary>Joins hard-wrapped lines of one paragraph, keeping breaks before list items.</summary>
    private static string JoinWrappedLines(string paragraph)
    {
        var lines = paragraph.Split('\n');
        var sb = new StringBuilder(paragraph.Length);
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (sb.Length == 0) { sb.Append(line); continue; }

            if (ListItemStart().IsMatch(line))
            {
                sb.Append('\n').Append(line);
            }
            else if (IsCjk(sb[^1]) && IsCjk(line[0]))
            {
                sb.Append(line);
            }
            else
            {
                sb.Append(' ').Append(line);
            }
        }
        return sb.ToString();
    }

    public static (string Text, bool Truncated) Limit(string text)
    {
        if (text.Length <= MaxLength) return (text, false);
        return (text[..MaxLength], true);
    }

    /// <summary>True for a single word or a short phrase (dictionary mode).</summary>
    public static bool LooksLikeWord(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 40) return false;
        var t = text.Trim().TrimEnd('.', ',', '!', '?', '。', '，', '！', '？', ':', '：');
        if (t.Length == 0 || t.IndexOfAny(new[] { '.', '!', '?', '。', '！', '？', ';', '；', '\n', ',', '，' }) >= 0) return false;
        if (LanguageInfo.DetectScript(t) is "zh-CN" or "ja") return t.Length <= 6;
        return t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 4;
    }

    private static bool IsCjk(char c) =>
        c is >= '　' and <= '鿿' or >= '＀' and <= '￯' or >= '가' and <= '힯';
}
