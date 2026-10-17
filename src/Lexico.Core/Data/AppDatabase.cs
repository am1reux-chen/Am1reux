using System.Globalization;
using Microsoft.Data.Sqlite;
using Lexico.Core.Domain;

namespace Lexico.Core.Data;

/// <summary>
/// Single-file SQLite store. All public work runs off the caller's thread
/// (Task.Run) and is serialized through one gate — SQLite connections are not
/// safe for concurrent use. WAL keeps readers cheap for search.
/// </summary>
public sealed partial class AppDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AppDatabase(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 15,
        }.ToString());
        _conn.Open();
        ExecutePragmas();
    }

    private void ExecutePragmas()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA temp_store=MEMORY;";
        cmd.ExecuteNonQuery();
    }

    public Task MigrateAsync() => RunAsync(() => Migrations.Apply(_conn));

    public Task BackupToAsync(string destinationPath) => RunAsync(() =>
    {
        if (File.Exists(destinationPath))
            File.Delete(destinationPath);
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destinationPath }.ToString());
        target.Open();
        _conn.BackupDatabase(target);
        return 0;
    });

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await _conn.DisposeAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
        _gate.Dispose();
    }

    private async Task RunAsync(Action work)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await Task.Run(work).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<T> RunAsync<T>(Func<T> work)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(work).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    // ---- helpers ----

    private static string Fmt(DateTime utc) => utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static DateTime ParseUtc(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private SqliteCommand Cmd(string sql, params (string Name, object? Value)[] args)
    {
        var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private T Scalar<T>(string sql, params (string, object?)[] args)
    {
        using var cmd = Cmd(sql, args);
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? default! : (T)Convert.ChangeType(result, typeof(T), CultureInfo.InvariantCulture);
    }

    private int Exec(string sql, params (string, object?)[] args)
    {
        using var cmd = Cmd(sql, args);
        return cmd.ExecuteNonQuery();
    }

    // ---- settings ----

    public Task<string?> GetSettingAsync(string key) => RunAsync(() =>
    {
        var v = Scalar<object?>("SELECT value FROM settings WHERE key = @k", ("@k", key));
        return v is null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
    });

    public Task SetSettingAsync(string key, string value) => RunAsync(() =>
    {
        Exec("INSERT INTO settings(key, value) VALUES(@k, @v) ON CONFLICT(key) DO UPDATE SET value = @v", ("@k", key), ("@v", value));
        return 0;
    });

    public async Task<int> GetSettingIntAsync(string key, int fallback)
    {
        var raw = await GetSettingAsync(key).ConfigureAwait(false);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    public async Task<double> GetSettingDoubleAsync(string key, double fallback)
    {
        var raw = await GetSettingAsync(key).ConfigureAwait(false);
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    // ---- words ----

    public Task<int> CountWordsAsync() => RunAsync(() => Scalar<long>("SELECT COUNT(*) FROM words") is long n ? (int)n : 0);

    /// <summary>Insert or update a word (keyed by lemma) and replace its senses. Returns the word id.</summary>
    public Task<long> UpsertWordAsync(WordEntry word) => RunAsync(() =>
    {
        using var tx = _conn.BeginTransaction();
        long wordId;
        using (var find = Cmd("SELECT id FROM words WHERE lemma = @l", ("@l", word.Lemma)))
        {
            find.Transaction = tx;
            var existing = find.ExecuteScalar();
            if (existing is null or DBNull)
            {
                using var ins = Cmd(
                    "INSERT INTO words(lemma, ipa_us, ipa_uk, frequency_rank, cefr) VALUES(@l, @us, @uk, @fr, @ce); SELECT last_insert_rowid();",
                    ("@l", word.Lemma), ("@us", word.IpaUs), ("@uk", word.IpaUk), ("@fr", word.FrequencyRank), ("@ce", word.Cefr));
                ins.Transaction = tx;
                wordId = Convert.ToInt64(ins.ExecuteScalar()!, CultureInfo.InvariantCulture);
            }
            else
            {
                wordId = Convert.ToInt64(existing, CultureInfo.InvariantCulture);
                using var upd = Cmd(
                    "UPDATE words SET ipa_us=@us, ipa_uk=@uk, frequency_rank=@fr, cefr=@ce WHERE id=@id",
                    ("@us", word.IpaUs), ("@uk", word.IpaUk), ("@fr", word.FrequencyRank), ("@ce", word.Cefr), ("@id", wordId));
                upd.Transaction = tx;
                upd.ExecuteNonQuery();
            }
        }
        using (var del = Cmd("DELETE FROM word_senses WHERE word_id = @id", ("@id", wordId)))
        {
            del.Transaction = tx;
            del.ExecuteNonQuery();
        }
        int order = 0;
        foreach (var s in word.Senses)
        {
            using var ins = Cmd(
                "INSERT INTO word_senses(word_id, pos, zh_gloss, en_gloss, example_en, example_zh, sense_order) VALUES(@w, @p, @z, @e, @xe, @xz, @o)",
                ("@w", wordId), ("@p", s.Pos), ("@z", s.ZhGloss), ("@e", s.EnGloss), ("@xe", s.ExampleEn), ("@xz", s.ExampleZh), ("@o", order++));
            ins.Transaction = tx;
            ins.ExecuteNonQuery();
        }
        tx.Commit();
        return wordId;
    });

    public Task<WordEntry?> GetWordAsync(long wordId) => RunAsync(() =>
    {
        using var cmd = Cmd("SELECT id, lemma, ipa_us, ipa_uk, frequency_rank, cefr FROM words WHERE id = @id", ("@id", wordId));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;
        var entry = ReadWord(reader);
        return entry with { Senses = LoadSenses(wordId) };
    });

    private WordEntry ReadWord(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetInt32(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        []);

    private List<WordSense> LoadSenses(long wordId)
    {
        var senses = new List<WordSense>();
        using var cmd = Cmd(
            "SELECT id, pos, zh_gloss, en_gloss, example_en, example_zh FROM word_senses WHERE word_id = @id ORDER BY sense_order",
            ("@id", wordId));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            senses.Add(new WordSense(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }
        return senses;
    }

    /// <summary>Prefix phrase search over FTS5, transparently falling back to LIKE.</summary>
    public Task<List<WordEntry>> SearchWordsAsync(string query, int limit = 30) => RunAsync(() =>
    {
        var sanitized = SanitizeFtsQuery(query);
        List<long> ids = [];
        if (!string.IsNullOrEmpty(sanitized))
        {
            try
            {
                using var cmd = Cmd(
                    "SELECT w.id FROM words_fts f JOIN words w ON w.id = f.rowid WHERE words_fts MATCH @m ORDER BY rank LIMIT @n",
                    ("@m", sanitized), ("@n", limit));
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    ids.Add(reader.GetInt64(0));
            }
            catch (SqliteException)
            {
                ids.Clear();
            }
        }
        if (ids.Count == 0 && !string.IsNullOrWhiteSpace(query))
        {
            using var cmd = Cmd(
                "SELECT id FROM words WHERE lemma LIKE @p || '%' ORDER BY frequency_rank IS NULL, frequency_rank LIMIT @n",
                ("@p", query.Trim()), ("@n", limit));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                ids.Add(reader.GetInt64(0));
        }
        return LoadWordsWithSensesCore(ids);
    });

    internal static string? SanitizeFtsQuery(string input)
    {
        var tokens = input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => new string(t.Where(char.IsLetterOrDigit).ToArray()))
            .Where(t => t.Length > 0)
            .ToList();
        if (tokens.Count == 0)
            return null;
        return "\"" + string.Join(" ", tokens) + "\"*";
    }

    public Task<List<WordEntry>> LoadWordsWithSenses(IReadOnlyList<long> ids) => RunAsync(() => LoadWordsWithSensesCore(ids));

    private List<WordEntry> LoadWordsWithSensesCore(IReadOnlyList<long> ids)
    {
        if (ids.Count == 0)
            return [];
        var placeholders = string.Join(",", Enumerable.Range(0, ids.Count).Select(i => "@i" + i));
        var args = ids.Select((id, i) => ($"@i{i}", (object?)id)).ToArray();
        var byId = new Dictionary<long, WordEntry>();
        using (var cmd = Cmd($"SELECT id, lemma, ipa_us, ipa_uk, frequency_rank, cefr FROM words WHERE id IN ({placeholders})", args))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var entry = ReadWord(reader);
                byId[entry.Id] = entry;
            }
        }
        foreach (var id in ids)
        {
            if (byId.TryGetValue(id, out var entry))
                byId[id] = entry with { Senses = LoadSenses(id) };
        }
        return ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
    }

    // ---- decks ----

    public Task<long> EnsureDeckAsync(string name, string? description, string? source, bool builtIn) => RunAsync(() =>
    {
        Exec(
            "INSERT INTO decks(name, description, source, built_in, created_at) VALUES(@n, @d, @s, @b, @c) ON CONFLICT(name) DO NOTHING",
            ("@n", name), ("@d", description), ("@s", source), ("@b", builtIn ? 1 : 0), ("@c", Fmt(DateTime.UtcNow)));
        return Scalar<long>("SELECT id FROM decks WHERE name = @n", ("@n", name));
    });

    public Task AddWordToDeckAsync(long deckId, long wordId, int position) => RunAsync(() =>
    {
        Exec(
            "INSERT INTO deck_words(deck_id, word_id, position) VALUES(@d, @w, @p) ON CONFLICT(deck_id, word_id) DO UPDATE SET position = @p",
            ("@d", deckId), ("@w", wordId), ("@p", position));
        return 0;
    });

    public Task<List<DeckInfo>> ListDecksAsync() => RunAsync(() =>
    {
        var decks = new List<DeckInfo>();
        using var cmd = Cmd("""
            SELECT d.id, d.name, d.description, d.source, d.built_in,
                   (SELECT COUNT(*) FROM deck_words dw WHERE dw.deck_id = d.id) AS word_count
            FROM decks d ORDER BY d.built_in DESC, d.name
            """);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            decks.Add(new DeckInfo(
                reader.GetInt64(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt64(4) != 0,
                (int)reader.GetInt64(5)));
        }
        return decks;
    });
}
