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
    public void Translation_prompt_contains_direction_rule()
    {
        var p = Prompts.TranslationSystem(Request);
        Assert.Contains("into Simplified Chinese", p);
        Assert.Contains("already written in Simplified Chinese, translate it into English", p);
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
