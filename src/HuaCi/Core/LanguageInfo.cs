namespace HuaCi.Core;

public sealed record LanguageInfo(string Code, string DisplayName, string EnglishName, string Voice)
{
    public static readonly IReadOnlyList<LanguageInfo> All = new[]
    {
        new LanguageInfo("zh-CN", "简体中文", "Simplified Chinese", "zh-CN-XiaoxiaoNeural"),
        new LanguageInfo("zh-TW", "繁體中文", "Traditional Chinese", "zh-TW-HsiaoChenNeural"),
        new LanguageInfo("en", "英语", "English", "en-US-AvaNeural"),
        new LanguageInfo("ja", "日语", "Japanese", "ja-JP-NanamiNeural"),
        new LanguageInfo("ko", "韩语", "Korean", "ko-KR-SunHiNeural"),
        new LanguageInfo("fr", "法语", "French", "fr-FR-DeniseNeural"),
        new LanguageInfo("de", "德语", "German", "de-DE-KatjaNeural"),
        new LanguageInfo("es", "西班牙语", "Spanish", "es-ES-ElviraNeural"),
        new LanguageInfo("it", "意大利语", "Italian", "it-IT-ElsaNeural"),
        new LanguageInfo("pt", "葡萄牙语", "Portuguese", "pt-BR-FranciscaNeural"),
        new LanguageInfo("ru", "俄语", "Russian", "ru-RU-SvetlanaNeural"),
        new LanguageInfo("ar", "阿拉伯语", "Arabic", "ar-SA-ZariyahNeural"),
        new LanguageInfo("th", "泰语", "Thai", "th-TH-PremwadeeNeural"),
        new LanguageInfo("vi", "越南语", "Vietnamese", "vi-VN-HoaiMyNeural"),
    };

    public const string UsEnglishVoice = "en-US-AvaNeural";
    public const string UkEnglishVoice = "en-GB-SoniaNeural";
    public const string MultilingualVoice = "en-US-AvaMultilingualNeural";

    /// <summary>Finds a language by code, matching "zh-Hans"/"zh" to zh-CN, "en-US" to en, etc.</summary>
    public static LanguageInfo? Find(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        code = code.Trim();
        var exact = All.FirstOrDefault(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        var lower = code.ToLowerInvariant();
        if (lower is "zh-hant" or "zh-hk" or "zh-mo" || lower.StartsWith("zh-hant")) return All[1];
        if (lower.StartsWith("zh")) return All[0];
        var primary = lower.Split('-', '_')[0];
        return All.FirstOrDefault(l => l.Code.Split('-')[0] == primary);
    }

    public static string NameOf(string? code) => Find(code)?.DisplayName ?? code ?? "";

    public static bool SameLanguage(string? a, string? b)
    {
        var la = Find(a);
        var lb = Find(b);
        if (la == null || lb == null) return false;
        // 简繁都算“中文”，避免把繁体中文当外文翻成简体后又走英文
        if (la.Code.StartsWith("zh") && lb.Code.StartsWith("zh")) return true;
        return la.Code == lb.Code;
    }

    private static readonly HashSet<string> LatinScript = new() { "en", "fr", "de", "es", "it", "pt", "vi" };

    /// <summary>
    /// Decides which language to translate into, given the language detected from the text.
    /// <see cref="DetectScript"/> reports every Latin-script text as "en", so when both the text and the target
    /// are Latin-script the answer is not <c>Certain</c>: the target is returned and the model must check
    /// whether the text is already in it. In every other case the target is decided here, because leaving the
    /// "already in the target language?" judgement to the model fails on mixed text (mostly Chinese with English words).
    /// </summary>
    public static (string Language, bool Certain) ResolveTarget(string detected, string target, string fallback)
    {
        var t = Find(target)?.Code ?? target;
        bool ambiguous = detected == "en" && LatinScript.Contains(t);
        if (ambiguous) return (target, false);
        return SameLanguage(detected, target) ? (fallback, true) : (target, true);
    }

    /// <summary>
    /// Picks the voice for reading text. <paramref name="accent"/> is "US" or "UK" and only affects English.
    /// </summary>
    public static string VoiceFor(string? languageCode, string accent = "US")
    {
        var lang = Find(languageCode);
        if (lang == null) return MultilingualVoice;
        if (lang.Code == "en") return accent.Equals("UK", StringComparison.OrdinalIgnoreCase) ? UkEnglishVoice : UsEnglishVoice;
        return lang.Voice;
    }

    /// <summary>
    /// Fast local guess of the language by writing system. Latin text is reported as "en"
    /// (the LLM's analysis later supplies the precise language).
    /// </summary>
    public static string DetectScript(string text)
    {
        int han = 0, kana = 0, hangul = 0, cyrillic = 0, arabic = 0, thai = 0, latin = 0;
        foreach (var ch in text)
        {
            switch (ch)
            {
                case >= '぀' and <= 'ヿ': kana++; break;
                case >= '一' and <= '鿿':
                case >= '㐀' and <= '䶿': han++; break;
                case >= '가' and <= '힯':
                case >= 'ᄀ' and <= 'ᇿ': hangul++; break;
                case >= 'Ѐ' and <= 'ӿ': cyrillic++; break;
                case >= '؀' and <= 'ۿ': arabic++; break;
                case >= '฀' and <= '๿': thai++; break;
                case >= 'A' and <= 'Z':
                case >= 'a' and <= 'z':
                case >= 'À' and <= 'ɏ': latin++; break;
            }
        }

        if (kana > 0 && kana * 5 >= han) return "ja";
        if (hangul > 0 && hangul >= han) return "ko";
        // 中文按字计，拉丁文按字母计；一个汉字约等于 3 个字母的信息量
        if (han > 0 && han * 3 >= latin) return "zh-CN";
        if (cyrillic > latin) return "ru";
        if (arabic > latin) return "ar";
        if (thai > latin) return "th";
        return "en";
    }
}
