using System.Text.Json.Serialization;

namespace HuaCi.Core;

public sealed class AppSettings
{
    public const string DefaultModel = "deepseek/deepseek-v4.1-flash";

    /// <summary>Plain-text key, only in memory. Persisted encrypted via <see cref="ApiKeyProtected"/>.</summary>
    [JsonIgnore]
    public string ApiKey { get; set; } = "";

    public string ApiKeyProtected { get; set; } = "";

    public string Model { get; set; } = DefaultModel;

    // 翻译方向：译成 TargetLanguage；原文已是目标语言时译成 FallbackLanguage
    public string TargetLanguage { get; set; } = "zh-CN";
    public string FallbackLanguage { get; set; } = "en";
    public string ExplainLanguage { get; set; } = "zh-CN";
    public string VocabLevel { get; set; } = "CET6";

    // 划词
    public bool SelectionIconEnabled { get; set; } = true;
    public bool RequireTextCursor { get; set; } = true;
    public double IconAutoHideSeconds { get; set; } = 4;
    public string Hotkey { get; set; } = "Alt+Q";
    public List<string> BlacklistProcesses { get; set; } = new();
    public Dictionary<string, string> CopyKeyOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WindowsTerminal"] = "Ctrl+Shift+C",
        ["conhost"] = "Ctrl+Shift+C",
        ["OpenConsole"] = "Ctrl+Shift+C",
        ["mintty"] = "Ctrl+Insert",
    };

    // 发音
    public string EnglishAccent { get; set; } = "US";
    public double SpeechRate { get; set; } = 1.0;
    public bool OfflineSpeechFallback { get; set; } = true;

    // 外观
    public string Theme { get; set; } = "System";
    public double FontSize { get; set; } = 14;
    public double PopupWidth { get; set; } = 440;

    public bool AutoStart { get; set; }
    public bool FirstRunDone { get; set; }

    public AppSettings Clone()
    {
        var c = (AppSettings)MemberwiseClone();
        c.BlacklistProcesses = new List<string>(BlacklistProcesses);
        c.CopyKeyOverrides = new Dictionary<string, string>(CopyKeyOverrides, StringComparer.OrdinalIgnoreCase);
        return c;
    }
}
