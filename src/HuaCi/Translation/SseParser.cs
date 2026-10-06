using System.Text.Json;

namespace HuaCi.Translation;

public enum SseLineKind { Ignore, Content, Done, Error }

public readonly record struct SseLine(SseLineKind Kind, string? Text = null);

/// <summary>Parses one line of an OpenAI-compatible chat completion SSE stream.</summary>
public static class SseParser
{
    public static SseLine Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith(':')) return new(SseLineKind.Ignore);
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return new(SseLineKind.Ignore);

        var payload = line.AsSpan(5).Trim().ToString();
        if (payload == "[DONE]") return new(SseLineKind.Done);

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err))
            {
                var msg = err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var m)
                    ? m.GetString()
                    : err.ToString();
                return new(SseLineKind.Error, msg);
            }

            if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var choice = choices[0];
                if (choice.TryGetProperty("delta", out var delta)
                    && delta.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.String)
                {
                    var text = content.GetString();
                    if (!string.IsNullOrEmpty(text)) return new(SseLineKind.Content, text);
                }
            }
        }
        catch (JsonException)
        {
            // 半截/非法 JSON 行直接忽略
        }

        return new(SseLineKind.Ignore);
    }
}
