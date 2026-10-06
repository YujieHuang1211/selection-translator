using HuaCi.Core;

namespace HuaCi.Tests;

public class TextNormalizerTests
{
    [Fact]
    public void Joins_pdf_hard_wrapped_lines_and_hyphenation()
    {
        var raw = "Machine transla-\ntion has improved\r\ndramatically in recent\nyears.";
        Assert.Equal("Machine translation has improved dramatically in recent years.", TextNormalizer.Normalize(raw));
    }

    [Fact]
    public void Keeps_paragraph_breaks()
    {
        var raw = "First paragraph\ncontinues here.\n\n  Second paragraph.";
        Assert.Equal("First paragraph continues here.\n\nSecond paragraph.", TextNormalizer.Normalize(raw));
    }

    [Fact]
    public void Joins_cjk_lines_without_space()
    {
        Assert.Equal("这是一个被换行的句子。", TextNormalizer.Normalize("这是一个被\n换行的句子。"));
    }

    [Fact]
    public void Keeps_list_items_on_their_own_lines()
    {
        var raw = "Steps:\n1. Open the file\n2. Save it";
        Assert.Equal("Steps:\n1. Open the file\n2. Save it", TextNormalizer.Normalize(raw));
    }

    [Fact]
    public void Collapses_spaces_and_removes_soft_hyphen_and_nbsp()
    {
        Assert.Equal("a b c", TextNormalizer.Normalize("a   b­   c"));
    }

    [Fact]
    public void Empty_input_gives_empty_string()
    {
        Assert.Equal("", TextNormalizer.Normalize(null));
        Assert.Equal("", TextNormalizer.Normalize("  \n "));
    }

    [Fact]
    public void Limit_truncates_long_text()
    {
        var (text, truncated) = TextNormalizer.Limit(new string('a', TextNormalizer.MaxLength + 10));
        Assert.True(truncated);
        Assert.Equal(TextNormalizer.MaxLength, text.Length);
    }

    [Theory]
    [InlineData("ubiquitous", true)]
    [InlineData("take off", true)]
    [InlineData("in a nutshell.", true)]
    [InlineData("This is a full sentence, with a comma.", false)]
    [InlineData("苹果", true)]
    [InlineData("这是一句完整的中文句子。", false)]
    [InlineData("e.g.", false)]
    public void LooksLikeWord(string text, bool expected) => Assert.Equal(expected, TextNormalizer.LooksLikeWord(text));
}

public class LanguageInfoTests
{
    [Theory]
    [InlineData("Hello world", "en")]
    [InlineData("你好，世界", "zh-CN")]
    [InlineData("これは日本語の文です", "ja")]
    [InlineData("漢字とひらがな", "ja")]
    [InlineData("안녕하세요", "ko")]
    [InlineData("Привет мир", "ru")]
    [InlineData("用 Python 写一个 script", "zh-CN")]
    [InlineData("The API returns JSON (数据)", "en")]
    public void DetectScript(string text, string expected) => Assert.Equal(expected, LanguageInfo.DetectScript(text));

    [Theory]
    [InlineData("zh", "zh-CN")]
    [InlineData("zh-Hans", "zh-CN")]
    [InlineData("zh-Hant", "zh-TW")]
    [InlineData("en-GB", "en")]
    [InlineData("EN", "en")]
    [InlineData("ja-JP", "ja")]
    public void Find_normalizes_codes(string input, string expected) => Assert.Equal(expected, LanguageInfo.Find(input)!.Code);

    [Fact]
    public void Unknown_code_returns_null() => Assert.Null(LanguageInfo.Find("xx-YY"));

    [Fact]
    public void Simplified_and_traditional_chinese_count_as_same_language()
    {
        Assert.True(LanguageInfo.SameLanguage("zh-CN", "zh-TW"));
        Assert.False(LanguageInfo.SameLanguage("en", "zh-CN"));
    }

    [Fact]
    public void VoiceFor_respects_english_accent()
    {
        Assert.Equal(LanguageInfo.UsEnglishVoice, LanguageInfo.VoiceFor("en", "US"));
        Assert.Equal(LanguageInfo.UkEnglishVoice, LanguageInfo.VoiceFor("en-US", "UK"));
        Assert.Equal("ja-JP-NanamiNeural", LanguageInfo.VoiceFor("ja", "UK"));
        Assert.Equal(LanguageInfo.MultilingualVoice, LanguageInfo.VoiceFor("sv"));
    }
}

public class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "huaci-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Round_trips_settings_and_encrypts_api_key()
    {
        var store = new SettingsStore(_dir);
        var s = store.Load();
        s.ApiKey = "sk-or-v1-test-secret";
        s.TargetLanguage = "ja";
        s.BlacklistProcesses.Add("game");
        store.Save(s);

        var json = File.ReadAllText(Path.Combine(_dir, "settings.json"));
        Assert.DoesNotContain("sk-or-v1-test-secret", json);

        var loaded = new SettingsStore(_dir).Load();
        Assert.Equal("sk-or-v1-test-secret", loaded.ApiKey);
        Assert.Equal("ja", loaded.TargetLanguage);
        Assert.Contains("game", loaded.BlacklistProcesses);
        Assert.True(loaded.CopyKeyOverrides.ContainsKey("windowsterminal"), "dictionary must stay case-insensitive after load");
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{ not json");
        var s = new SettingsStore(_dir).Load();
        Assert.Equal(AppSettings.DefaultModel, s.Model);
    }

    [Fact]
    public void Defaults_match_requested_behaviour()
    {
        var s = new AppSettings();
        Assert.Equal("deepseek/deepseek-v4.1-flash", s.Model);
        Assert.Equal("zh-CN", s.TargetLanguage);
        Assert.Equal("en", s.FallbackLanguage);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
