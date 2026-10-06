using System.Text.Json.Nodes;
using HuaCi.Translation;

namespace HuaCi.Tests;

public class SseParserTests
{
    [Fact]
    public void Parses_content_delta()
    {
        var line = "data: {\"id\":\"x\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"你好\"}}]}";
        var r = SseParser.Parse(line);
        Assert.Equal(SseLineKind.Content, r.Kind);
        Assert.Equal("你好", r.Text);
    }

    [Theory]
    [InlineData(": OPENROUTER PROCESSING")]
    [InlineData("")]
    [InlineData("event: ping")]
    [InlineData("data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}")]
    [InlineData("data: {\"choices\":[{\"delta\":{\"content\":null}}]}")]
    [InlineData("data: {broken")]
    public void Ignores_comments_roles_and_garbage(string line) => Assert.Equal(SseLineKind.Ignore, SseParser.Parse(line).Kind);

    [Fact]
    public void Recognizes_done() => Assert.Equal(SseLineKind.Done, SseParser.Parse("data: [DONE]").Kind);

    [Fact]
    public void Surfaces_midstream_error()
    {
        var r = SseParser.Parse("data: {\"error\":{\"code\":502,\"message\":\"Provider returned error\"}}");
        Assert.Equal(SseLineKind.Error, r.Kind);
        Assert.Equal("Provider returned error", r.Text);
    }
}

public class JsonExtractTests
{
    private const string Sample = """
        {"source_lang":"en","mode":"text","keywords":[
          {"word":"ubiquitous","lemma":"ubiquitous","phonetic_us":"/juːˈbɪkwɪtəs/","phonetic_uk":"[juːˈbɪkwɪtəs]",
           "pos":"adj.","brief":"无处不在的","context_meaning":"指计算无处不在",
           "senses":[{"pos":"adj.","meaning":"无处不在的","example":"Phones are ubiquitous.","example_translation":"手机无处不在。"},
                     {"pos":"adj.","meaning":"","example":"","example_translation":""}],
           "forms":"","collocations":["ubiquitous computing"]},
          {"word":"","lemma":"","phonetic_us":"","phonetic_uk":"","pos":"","brief":"","context_meaning":"","senses":[],"forms":"","collocations":[]}
        ]}
        """;

    [Fact]
    public void Parses_analysis_and_cleans_it()
    {
        var a = JsonExtract.ParseAnalysis(Sample);
        Assert.Equal("en", a.SourceLang);
        Assert.False(a.IsWordMode);
        var k = Assert.Single(a.Keywords);           // empty entry removed
        Assert.Equal("juːˈbɪkwɪtəs", k.PhoneticUs);   // slashes stripped
        Assert.Equal("juːˈbɪkwɪtəs", k.PhoneticUk);   // brackets stripped
        Assert.Single(k.Senses);                       // empty sense removed
        Assert.Equal("ubiquitous computing", k.Collocations[0]);
    }

    [Fact]
    public void Tolerates_code_fences_and_chatter()
    {
        var raw = "Sure! Here is the JSON:\n```json\n" + Sample + "\n```\nHope it helps.";
        Assert.Single(JsonExtract.ParseAnalysis(raw).Keywords);
    }

    [Fact]
    public void Tolerates_missing_fields_and_unknown_mode()
    {
        var a = JsonExtract.ParseAnalysis("{\"mode\":\"weird\",\"keywords\":[{\"word\":\"run\"}]}");
        Assert.Equal("text", a.Mode);
        Assert.Equal("run", a.Keywords[0].Word);
        Assert.Empty(a.Keywords[0].Senses);
    }

    [Fact]
    public void Throws_FormatException_when_no_json() =>
        Assert.Throws<FormatException>(() => JsonExtract.ParseAnalysis("no json here"));
}

public class PromptsTests
{
    private static readonly TranslationRequest Request = new("Hello", "zh-CN", "en", "zh-CN", "CET6", "deepseek/deepseek-v4.1-flash");

    [Fact]
    public void English_text_gets_an_explicit_chinese_target()
    {
        var p = Prompts.TranslationSystem(Request);
        Assert.Contains("Translate it into Simplified Chinese.", p);
        Assert.DoesNotContain("already written", p);
    }

    [Fact]
    public void Mostly_chinese_text_with_english_words_is_translated_into_english()
    {
        // 回归用例：中文为主、夹杂英文单词时，模型曾把它“翻译”成中文
        var r = Request with { Text = "我的意思是如果我们朋友想要从github中下载这个软件并使用他们应该如何使用" };
        var p = Prompts.TranslationSystem(r);
        Assert.Contains("Translate it into English.", p);
        Assert.DoesNotContain("already written", p);
    }

    [Fact]
    public void Japanese_text_gets_explicit_target()
    {
        var p = Prompts.TranslationSystem(Request with { Text = "これは日本語の文です" });
        Assert.Contains("Translate it into Simplified Chinese.", p);
    }

    [Fact]
    public void Latin_text_with_latin_target_keeps_the_model_side_rule()
    {
        // 法语/英语等拉丁字母文本本地分不清，目标又是拉丁语言时仍交给模型判断
        var p = Prompts.TranslationSystem(Request with { Text = "Bonjour tout le monde", TargetLanguage = "en", FallbackLanguage = "zh-CN" });
        Assert.Contains("Translate it into English. If the text is already written in English, translate it into Simplified Chinese instead.", p);
    }

    [Fact]
    public void Analysis_prompt_mentions_level_and_explain_language()
    {
        var p = Prompts.AnalysisSystem(Request);
        Assert.Contains("CET-6", p);
        Assert.Contains("written in Simplified Chinese", p);
    }

    [Fact]
    public void Schema_is_strict_with_all_properties_required()
    {
        var schema = Prompts.AnalysisSchema();
        AssertStrict(schema);
        var word = schema["properties"]!["keywords"]!["items"]!.AsObject();
        AssertStrict(word);
        AssertStrict(word["properties"]!["senses"]!["items"]!.AsObject());
    }

    private static void AssertStrict(JsonObject obj)
    {
        Assert.False(obj["additionalProperties"]!.GetValue<bool>());
        var props = obj["properties"]!.AsObject().Select(p => p.Key).OrderBy(x => x);
        var required = obj["required"]!.AsArray().Select(x => x!.GetValue<string>()).OrderBy(x => x);
        Assert.Equal(props, required);
    }

    [Fact]
    public void Cache_key_changes_with_any_setting()
    {
        var other = Request with { TargetLanguage = "ja" };
        Assert.NotEqual(Request.CacheKey, other.CacheKey);
        Assert.Equal(Request.CacheKey, (Request with { }).CacheKey);
    }

    [Fact]
    public void Friendly_errors_are_in_chinese()
    {
        Assert.Contains("API Key", OpenRouterClient.FriendlyError(System.Net.HttpStatusCode.Unauthorized, ""));
        Assert.Contains("余额不足", OpenRouterClient.FriendlyError(System.Net.HttpStatusCode.PaymentRequired, ""));
        Assert.Contains("限流", OpenRouterClient.FriendlyError(System.Net.HttpStatusCode.TooManyRequests, ""));
    }
}

public class IncrementalAnalysisParserTests
{
    private const string Full = """
        {"source_lang":"en","mode":"text","keywords":[{"word":"a{b}","lemma":"x","phonetic_us":"/eɪ/","phonetic_uk":"","pos":"n.","brief":"含 \"引号\" 和 ] 括号","context_meaning":"","senses":[{"pos":"n.","meaning":"m1","example":"e","example_translation":"t"}],"forms":"","collocations":[]},{"word":"second","lemma":"second","phonetic_us":"","phonetic_uk":"","pos":"adj.","brief":"第二","context_meaning":"","senses":[],"forms":"","collocations":["c"]}]}
        """;

    [Fact]
    public void Emits_each_keyword_once_as_soon_as_it_is_complete()
    {
        var p = new IncrementalAnalysisParser();
        var emitted = new List<WordEntry>();
        // 逐字符喂入，模拟最碎的流式分块
        foreach (var ch in Full) emitted.AddRange(p.Feed(ch.ToString()));

        Assert.Equal(new[] { "a{b}", "second" }, emitted.Select(e => e.Word));
        Assert.Equal("eɪ", emitted[0].PhoneticUs);
        Assert.Equal("en", p.SourceLang);
        Assert.Equal("text", p.Mode);
    }

    [Fact]
    public void First_keyword_is_available_before_the_stream_ends()
    {
        var p = new IncrementalAnalysisParser();
        var cut = Full.IndexOf("{\"word\":\"second\"", StringComparison.Ordinal);
        var first = p.Feed(Full[..cut]);
        Assert.Single(first);
        Assert.Empty(p.Feed(Full[cut..(cut + 20)]));
        Assert.Single(p.Feed(Full[(cut + 20)..]));
    }

    [Fact]
    public void No_keywords_array_yet_returns_nothing()
    {
        var p = new IncrementalAnalysisParser();
        Assert.Empty(p.Feed("{\"source_lang\":\"ja\",\"mo"));
        Assert.Equal("ja", p.SourceLang);
        Assert.Null(p.Mode);
    }
}
