using HuaCi.Data;
using HuaCi.Translation;

namespace HuaCi.Tests;

public class HistoryStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "huaci-db-" + Guid.NewGuid().ToString("N"));
    private readonly HistoryStore _store;

    public HistoryStoreTests()
    {
        _store = new HistoryStore(Path.Combine(_dir, "test.db"));
    }

    private static HistoryEntry Entry(string text, string key) => new()
    {
        Text = text, Translation = "译文 " + text, CacheKey = key, CreatedAt = DateTime.Now, Model = "m",
    };

    [Fact]
    public void Cache_lookup_returns_latest_and_deduplicates()
    {
        _store.AddHistory(Entry("hello", "k1"));
        var second = Entry("hello", "k1");
        second.Translation = "新译文";
        _store.AddHistory(second);

        Assert.Equal("新译文", _store.FindByCacheKey("k1")!.Translation);
        Assert.Single(_store.SearchHistory(null));
        Assert.Null(_store.FindByCacheKey("missing"));
    }

    [Fact]
    public void Search_matches_text_and_translation_and_escapes_wildcards()
    {
        _store.AddHistory(Entry("apple pie", "a"));
        _store.AddHistory(Entry("100% sure", "b"));
        Assert.Single(_store.SearchHistory("apple"));
        Assert.Single(_store.SearchHistory("译文 apple"));
        Assert.Single(_store.SearchHistory("100%"));
        Assert.Equal(2, _store.SearchHistory("").Count);
    }

    [Fact]
    public void Vocab_add_is_case_insensitive_upsert_and_exports()
    {
        var entry = new WordEntry
        {
            Word = "Running", Lemma = "run", PhoneticUs = "rʌn", Pos = "v.", Brief = "跑",
            Senses = { new Sense { Pos = "v.", Meaning = "跑步", Example = "I run daily.", ExampleTranslation = "我每天跑步。" } },
        };
        _store.AddVocab(entry, "en", "He is running.");
        _store.AddVocab(entry, "en", "He keeps running.");

        Assert.True(_store.IsInVocab("RUN"));
        var list = _store.ListVocab();
        var v = Assert.Single(list);
        Assert.Equal("run", v.Word);
        Assert.Equal("He keeps running.", v.SourceSentence);
        Assert.Equal("跑步", v.Entry!.Senses[0].Meaning);

        var csv = Path.Combine(_dir, "v.csv");
        _store.ExportVocabCsv(csv);
        Assert.Contains("\"run\"", File.ReadAllText(csv));

        var anki = Path.Combine(_dir, "v.txt");
        _store.ExportVocabAnki(anki);
        var lines = File.ReadAllLines(anki);
        Assert.Equal("#separator:tab", lines[0]);
        Assert.Contains("\t", lines[2]);

        _store.RemoveVocab("Run");
        Assert.Empty(_store.ListVocab());
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }
}
