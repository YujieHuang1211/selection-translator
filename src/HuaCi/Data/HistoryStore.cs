using System.IO;
using System.Text;
using System.Text.Json;
using HuaCi.Translation;
using Microsoft.Data.Sqlite;

namespace HuaCi.Data;

public sealed class HistoryEntry
{
    public long Id { get; set; }
    public string Text { get; set; } = "";
    public string Translation { get; set; } = "";
    public string AnalysisJson { get; set; } = "";
    public string SourceLang { get; set; } = "";
    public string TargetLang { get; set; } = "";
    public string Model { get; set; } = "";
    public string CacheKey { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public sealed class VocabEntry
{
    public long Id { get; set; }
    public string Word { get; set; } = "";
    public string Lang { get; set; } = "";
    public string Phonetic { get; set; } = "";
    public string Brief { get; set; } = "";
    public string EntryJson { get; set; } = "";
    public string SourceSentence { get; set; } = "";
    public DateTime CreatedAt { get; set; }

    public WordEntry? Entry
    {
        get
        {
            try { return JsonSerializer.Deserialize<WordEntry>(EntryJson); }
            catch (JsonException) { return null; }
        }
    }
}

/// <summary>SQLite store for translation history (also the result cache) and the vocabulary book.</summary>
public sealed class HistoryStore
{
    private const int MaxHistory = 5000;
    private readonly string _connectionString;

    public HistoryStore(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath, Cache = SqliteCacheMode.Shared }.ToString();
        using var c = Open();
        Exec(c, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS history(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                text TEXT NOT NULL,
                translation TEXT NOT NULL,
                analysis_json TEXT NOT NULL DEFAULT '',
                source_lang TEXT NOT NULL DEFAULT '',
                target_lang TEXT NOT NULL DEFAULT '',
                model TEXT NOT NULL DEFAULT '',
                cache_key TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_history_cache ON history(cache_key);
            CREATE TABLE IF NOT EXISTS vocab(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                word TEXT NOT NULL COLLATE NOCASE UNIQUE,
                lang TEXT NOT NULL DEFAULT '',
                phonetic TEXT NOT NULL DEFAULT '',
                brief TEXT NOT NULL DEFAULT '',
                entry_json TEXT NOT NULL DEFAULT '',
                source_sentence TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL);
            """);
    }

    public event Action? VocabChanged;

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ---------------- history ----------------

    public void AddHistory(HistoryEntry e)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using (var del = c.CreateCommand())
        {
            // 同一缓存键只保留最新一条，避免历史里重复
            del.CommandText = "DELETE FROM history WHERE cache_key = $k";
            del.Parameters.AddWithValue("$k", e.CacheKey);
            del.ExecuteNonQuery();
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO history(text, translation, analysis_json, source_lang, target_lang, model, cache_key, created_at)
                VALUES($t, $tr, $a, $s, $tl, $m, $k, $c)
                """;
            cmd.Parameters.AddWithValue("$t", e.Text);
            cmd.Parameters.AddWithValue("$tr", e.Translation);
            cmd.Parameters.AddWithValue("$a", e.AnalysisJson);
            cmd.Parameters.AddWithValue("$s", e.SourceLang);
            cmd.Parameters.AddWithValue("$tl", e.TargetLang);
            cmd.Parameters.AddWithValue("$m", e.Model);
            cmd.Parameters.AddWithValue("$k", e.CacheKey);
            cmd.Parameters.AddWithValue("$c", e.CreatedAt.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        using (var trim = c.CreateCommand())
        {
            trim.CommandText = $"DELETE FROM history WHERE id NOT IN (SELECT id FROM history ORDER BY id DESC LIMIT {MaxHistory})";
            trim.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public HistoryEntry? FindByCacheKey(string key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM history WHERE cache_key = $k ORDER BY id DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$k", key);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadHistory(r) : null;
    }

    public List<HistoryEntry> SearchHistory(string? query, int limit = 200)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        if (string.IsNullOrWhiteSpace(query))
        {
            cmd.CommandText = "SELECT * FROM history ORDER BY id DESC LIMIT $n";
        }
        else
        {
            cmd.CommandText = "SELECT * FROM history WHERE text LIKE $q ESCAPE '\\' OR translation LIKE $q ESCAPE '\\' ORDER BY id DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$q", "%" + EscapeLike(query.Trim()) + "%");
        }
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<HistoryEntry>();
        while (r.Read()) list.Add(ReadHistory(r));
        return list;
    }

    public void DeleteHistory(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM history WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void ClearHistory()
    {
        using var c = Open();
        Exec(c, "DELETE FROM history");
    }

    private static HistoryEntry ReadHistory(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("id")),
        Text = r.GetString(r.GetOrdinal("text")),
        Translation = r.GetString(r.GetOrdinal("translation")),
        AnalysisJson = r.GetString(r.GetOrdinal("analysis_json")),
        SourceLang = r.GetString(r.GetOrdinal("source_lang")),
        TargetLang = r.GetString(r.GetOrdinal("target_lang")),
        Model = r.GetString(r.GetOrdinal("model")),
        CacheKey = r.GetString(r.GetOrdinal("cache_key")),
        CreatedAt = DateTime.Parse(r.GetString(r.GetOrdinal("created_at"))),
    };

    // ---------------- vocab ----------------

    public void AddVocab(WordEntry entry, string lang, string sourceSentence)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO vocab(word, lang, phonetic, brief, entry_json, source_sentence, created_at)
            VALUES($w, $l, $p, $b, $j, $s, $c)
            ON CONFLICT(word) DO UPDATE SET lang=$l, phonetic=$p, brief=$b, entry_json=$j, source_sentence=$s
            """;
        var word = string.IsNullOrWhiteSpace(entry.Lemma) ? entry.Word : entry.Lemma;
        cmd.Parameters.AddWithValue("$w", word.Trim());
        cmd.Parameters.AddWithValue("$l", lang);
        cmd.Parameters.AddWithValue("$p", string.IsNullOrEmpty(entry.PhoneticUs) ? entry.PhoneticUk : entry.PhoneticUs);
        cmd.Parameters.AddWithValue("$b", (entry.Pos + " " + entry.Brief).Trim());
        cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(entry));
        cmd.Parameters.AddWithValue("$s", sourceSentence);
        cmd.Parameters.AddWithValue("$c", DateTime.Now.ToString("O"));
        cmd.ExecuteNonQuery();
        VocabChanged?.Invoke();
    }

    public void RemoveVocab(string word)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM vocab WHERE word = $w";
        cmd.Parameters.AddWithValue("$w", word.Trim());
        cmd.ExecuteNonQuery();
        VocabChanged?.Invoke();
    }

    public bool IsInVocab(string word)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM vocab WHERE word = $w LIMIT 1";
        cmd.Parameters.AddWithValue("$w", word.Trim());
        return cmd.ExecuteScalar() != null;
    }

    public List<VocabEntry> ListVocab(string? query = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        if (string.IsNullOrWhiteSpace(query))
        {
            cmd.CommandText = "SELECT * FROM vocab ORDER BY id DESC";
        }
        else
        {
            cmd.CommandText = "SELECT * FROM vocab WHERE word LIKE $q ESCAPE '\\' OR brief LIKE $q ESCAPE '\\' ORDER BY id DESC";
            cmd.Parameters.AddWithValue("$q", "%" + EscapeLike(query.Trim()) + "%");
        }
        using var r = cmd.ExecuteReader();
        var list = new List<VocabEntry>();
        while (r.Read())
        {
            list.Add(new VocabEntry
            {
                Id = r.GetInt64(r.GetOrdinal("id")),
                Word = r.GetString(r.GetOrdinal("word")),
                Lang = r.GetString(r.GetOrdinal("lang")),
                Phonetic = r.GetString(r.GetOrdinal("phonetic")),
                Brief = r.GetString(r.GetOrdinal("brief")),
                EntryJson = r.GetString(r.GetOrdinal("entry_json")),
                SourceSentence = r.GetString(r.GetOrdinal("source_sentence")),
                CreatedAt = DateTime.Parse(r.GetString(r.GetOrdinal("created_at"))),
            });
        }
        return list;
    }

    /// <summary>CSV (UTF-8 with BOM so Excel opens Chinese correctly).</summary>
    public void ExportVocabCsv(string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("单词,音标,释义,例句,原句,添加时间");
        foreach (var v in ListVocab())
        {
            var example = FirstExample(v);
            sb.AppendLine(string.Join(',', Csv(v.Word), Csv(v.Phonetic), Csv(v.Brief), Csv(example), Csv(v.SourceSentence), Csv(v.CreatedAt.ToString("yyyy-MM-dd HH:mm"))));
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    /// <summary>Tab-separated "front back" file that Anki imports directly (HTML enabled).</summary>
    public void ExportVocabAnki(string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#separator:tab");
        sb.AppendLine("#html:true");
        foreach (var v in ListVocab())
        {
            var front = Html(v.Word) + (string.IsNullOrEmpty(v.Phonetic) ? "" : $"<br><span style=\"color:#888\">/{Html(v.Phonetic)}/</span>");
            var back = Html(v.Brief);
            var e = v.Entry;
            if (e != null)
            {
                foreach (var s in e.Senses.Take(3))
                {
                    back += $"<br><b>{Html(s.Pos)}</b> {Html(s.Meaning)}";
                    if (!string.IsNullOrEmpty(s.Example)) back += $"<br><i>{Html(s.Example)}</i><br>{Html(s.ExampleTranslation)}";
                }
            }
            if (!string.IsNullOrEmpty(v.SourceSentence)) back += $"<hr><small>{Html(v.SourceSentence)}</small>";
            sb.Append(front.Replace('\t', ' ')).Append('\t').AppendLine(back.Replace('\t', ' ').Replace("\n", "<br>"));
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static string FirstExample(VocabEntry v)
    {
        var s = v.Entry?.Senses.FirstOrDefault(x => !string.IsNullOrEmpty(x.Example));
        return s == null ? "" : $"{s.Example} {s.ExampleTranslation}".Trim();
    }

    private static string Csv(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";

    private static string Html(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");

    private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
