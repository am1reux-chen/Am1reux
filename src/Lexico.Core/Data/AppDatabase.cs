using System.Globalization;
using System.Text;
using System.Text.Json;
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
    private static readonly (string Name, string[] Columns)[] RequiredSchema =
    [
        ("schema_migrations", ["version", "applied_at"]),
        ("words", ["id", "lemma", "ipa_us", "ipa_uk", "frequency_rank", "cefr", "details_json"]),
        ("word_senses", ["id", "word_id", "pos", "zh_gloss", "en_gloss", "example_en", "example_zh", "sense_order"]),
        ("decks", ["id", "name", "description", "source", "built_in", "created_at"]),
        ("deck_words", ["deck_id", "word_id", "position"]),
        ("cards", ["id", "word_id", "deck_id", "state", "step", "stability", "difficulty", "due", "last_review", "reps", "lapses", "created_at"]),
        ("card_marks", ["card_id", "favorite", "wrong", "mastered"]),
        ("articles", ["id", "title", "title_translation", "created_at", "updated_at"]),
        ("article_sentences", ["id", "article_id", "position", "text", "translation"]),
        ("article_progress", ["article_id", "current_sentence", "mode", "is_complete", "updated_at"]),
        ("review_logs", ["id", "card_id", "reviewed_at", "rating", "state_before", "step_before", "stability_before", "difficulty_before", "scheduled_days_before", "reps_before", "raw_elapsed_ms", "elapsed_ms", "timing_valid", "fsrs_retrievability", "engine_version", "next_due", "next_interval_days", "hint_used", "session_id"]),
        ("sessions", ["id", "started_at", "ended_at", "intensity", "reviews", "again", "hard", "good", "easy", "planned_cards", "new_count", "estimated_minutes", "study_mode"]),
        ("daily_plans", ["id", "date", "intensity", "review_count", "new_count", "estimated_minutes", "created_at"]),
        ("model_profiles", ["id", "kind", "version", "weights", "trained_at", "metrics", "is_active", "trained_through_log_id", "accepted"]),
        ("model_predictions", ["id", "review_log_id", "profile_id", "predicted_recall", "created_at", "raw_recall", "model_version", "features_json"]),
        ("settings", ["key", "value"]),
        ("session_queue", ["id", "session_id", "card_id", "position", "is_early_review"]),
        ("words_fts", ["lemma"]),
    ];
    private static readonly (string Name, string Table, string Sql)[] RequiredTriggers =
    [
        ("words_ai", "words", """
            CREATE TRIGGER words_ai AFTER INSERT ON words BEGIN
                INSERT INTO words_fts(rowid, lemma) VALUES (new.id, new.lemma);
            END
            """),
        ("words_ad", "words", """
            CREATE TRIGGER words_ad AFTER DELETE ON words BEGIN
                INSERT INTO words_fts(words_fts, rowid, lemma) VALUES('delete', old.id, old.lemma);
            END
            """),
        ("words_au", "words", """
            CREATE TRIGGER words_au AFTER UPDATE ON words BEGIN
                INSERT INTO words_fts(words_fts, rowid, lemma) VALUES('delete', old.id, old.lemma);
                INSERT INTO words_fts(rowid, lemma) VALUES (new.id, new.lemma);
            END
            """),
        ("review_logs_no_update", "review_logs", """
            CREATE TRIGGER review_logs_no_update BEFORE UPDATE ON review_logs BEGIN
                SELECT RAISE(ABORT, 'review_logs are immutable');
            END
            """),
        ("review_logs_no_delete", "review_logs", """
            CREATE TRIGGER review_logs_no_delete BEFORE DELETE ON review_logs BEGIN
                SELECT RAISE(ABORT, 'review_logs are immutable');
            END
            """),
    ];
    private static readonly (string Name, string Table, bool Unique, string[] Columns)[] RequiredIndexes =
    [
        ("idx_words_lemma", "words", true, ["lemma"]),
        ("idx_senses_word", "word_senses", false, ["word_id"]),
        ("idx_deck_words_word", "deck_words", false, ["word_id"]),
        ("idx_cards_due", "cards", false, ["state", "due"]),
        ("idx_cards_word", "cards", false, ["word_id"]),
        ("idx_logs_card", "review_logs", false, ["card_id"]),
        ("idx_logs_time", "review_logs", false, ["reviewed_at"]),
        ("idx_predictions_log", "model_predictions", false, ["review_log_id"]),
        ("idx_logs_session", "review_logs", false, ["session_id"]),
        ("idx_session_queue_order", "session_queue", false, ["session_id", "position"]),
        ("idx_article_sentences_order", "article_sentences", false, ["article_id", "position"]),
    ];
    private static readonly Lazy<Dictionary<string, string>> CanonicalTableDefinitions = new(BuildCanonicalTableDefinitions);

    private readonly SqliteConnection _conn;
    private readonly string _databasePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lifetimeLock = new();
    private int _pendingOperations;
    private bool _disposeStarted;
    private TaskCompletionSource? _operationsDrained;
    private Task? _disposeTask;

    public AppDatabase(string path)
    {
        _databasePath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 15,
            Pooling = false,
        }.ToString());
        try
        {
            _conn.Open();
            ExecutePragmas();
        }
        catch
        {
            _conn.Dispose();
            throw;
        }
    }

    private void ExecutePragmas()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA temp_store=MEMORY;";
        cmd.ExecuteNonQuery();
    }

    public Task MigrateAsync() => RunAsync(() => Migrations.Apply(_conn));

    /// <summary>Re-checks the complete Lexio schema and database integrity on the serialized DB worker.</summary>
    public Task ValidateAsync() => RunAsync(() =>
    {
        CheckDatabase(_conn);
        return 0;
    });

    /// <summary>Open, migrate, and validate the primary database, recovering from a validated backup when needed.</summary>
    public static Task<AppDatabase> OpenWithRecoveryAsync(string path, string backupPath)
        => Task.Run(() => OpenWithRecovery(path, backupPath));

    private static AppDatabase OpenWithRecovery(string path, string backupPath)
    {
        var databasePath = Path.GetFullPath(path);
        var backupFile = Path.GetFullPath(backupPath);
        if (string.Equals(databasePath, backupFile, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("主数据库和自动备份不能是同一个文件", nameof(backupPath));

        AppDatabase? database = null;
        try
        {
            database = new AppDatabase(databasePath);
            database.MigrateAsync().GetAwaiter().GetResult();
            database.ValidateForStartup();
            return database;
        }
        catch (Exception error) when (IsRecoverableDatabaseError(error))
        {
            if (database is not null)
                database.DisposeAsync().AsTask().GetAwaiter().GetResult();

            if (!File.Exists(backupFile))
                throw new InvalidDataException("Lexio 主数据库无法通过完整性检查，且没有可用备份", error);

            try
            {
                RestoreBackupFile(databasePath, backupFile);
            }
            catch (Exception backupError) when (IsRecoverableDatabaseError(backupError))
            {
                throw new InvalidDataException("Lexio 自动备份无效，原主数据库文件已保留", backupError);
            }
            var recovered = new AppDatabase(databasePath);
            try
            {
                recovered.MigrateAsync().GetAwaiter().GetResult();
                recovered.ValidateForStartup();
                return recovered;
            }
            catch
            {
                recovered.DisposeAsync().AsTask().GetAwaiter().GetResult();
                throw;
            }
        }
    }

    private void ValidateForStartup()
        => RunAsync(() =>
        {
            CheckDatabase(_conn);
            return 0;
        }).GetAwaiter().GetResult();

    private static bool IsRecoverableDatabaseError(Exception error)
        => error is InvalidDataException ||
           error is SqliteException { SqliteErrorCode: 1 or 11 or 26 };

    private static void RestoreBackupFile(string databasePath, string backupPath)
    {
        var stagingPath = databasePath + ".recovery-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = backupPath,
                Mode = SqliteOpenMode.ReadOnly,
                DefaultTimeout = 15,
                Pooling = false,
            }.ToString()))
            {
                source.Open();
                using (var check = source.CreateCommand())
                {
                    check.CommandText = "PRAGMA quick_check";
                    if (!string.Equals(Convert.ToString(check.ExecuteScalar(), CultureInfo.InvariantCulture), "ok", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("自动备份完整性检查失败");
                }
                using (var check = source.CreateCommand())
                {
                    check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='schema_migrations'";
                    if (Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                        throw new InvalidDataException("自动备份不包含 Lexio 数据库结构");
                }

                using var staging = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = stagingPath,
                    Pooling = false,
                }.ToString());
                staging.Open();
                source.BackupDatabase(staging);
                Migrations.Apply(staging);
                CheckDatabase(staging);
            }

            ReplaceWithPreservedPrimary(databasePath, stagingPath);
        }
        finally
        {
            TryDeleteDatabaseFiles(stagingPath);
        }
    }

    private static void ReplaceWithPreservedPrimary(string databasePath, string stagingPath)
    {
        var preservedPath = databasePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture)
                            + "-" + Guid.NewGuid().ToString("N");
        var movedFiles = new List<(string Original, string Preserved)>();
        try
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var original = databasePath + suffix;
                if (!File.Exists(original))
                    continue;
                var preserved = preservedPath + suffix;
                File.Move(original, preserved);
                movedFiles.Add((original, preserved));
            }
            File.Move(stagingPath, databasePath);
        }
        catch
        {
            for (int i = movedFiles.Count - 1; i >= 0; i--)
            {
                var (original, preserved) = movedFiles[i];
                if (File.Exists(preserved) && !File.Exists(original))
                    File.Move(preserved, original);
            }
            throw;
        }
    }

    private static void CheckDatabase(SqliteConnection connection)
    {
        using var integrity = connection.CreateCommand();
        integrity.CommandText = "PRAGMA quick_check";
        if (!string.Equals(Convert.ToString(integrity.ExecuteScalar(), CultureInfo.InvariantCulture), "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("数据库完整性检查失败");
        CheckRequiredSchemaObjects(connection);
        using (var migrations = connection.CreateCommand())
        {
            migrations.CommandText = "SELECT COUNT(*), COALESCE(MIN(version), 0), COALESCE(MAX(version), 0) FROM schema_migrations";
            using var reader = migrations.ExecuteReader();
            reader.Read();
            if (reader.GetInt32(0) != Migrations.All.Length ||
                reader.GetInt32(1) != Migrations.All[0].Version ||
                reader.GetInt32(2) != Migrations.All[^1].Version)
                throw new InvalidDataException("数据库迁移版本记录与当前 Lexio schema 不匹配");
        }
        CheckForeignKeys(connection);
    }

    public Task BackupToAsync(string destinationPath) => RunAsync(() =>
    {
        var destination = Path.GetFullPath(destinationPath);
        EnsureDifferentDatabase(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var stagingPath = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = stagingPath, Pooling = false }.ToString()))
            {
                target.Open();
                _conn.BackupDatabase(target);
            }
            using (var verify = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = stagingPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString()))
            {
                verify.Open();
                CheckDatabase(verify);
            }
            File.Move(stagingPath, destination, overwrite: true);
        }
        finally
        {
            TryDeleteDatabaseFiles(stagingPath);
        }
        return 0;
    });

    public Task RestoreFromAsync(string sourcePath) => RunAsync(() =>
    {
        var sourceFile = Path.GetFullPath(sourcePath);
        EnsureDifferentDatabase(sourceFile);
        var stagingPath = _databasePath + ".restore-" + Guid.NewGuid().ToString("N");
        try
        {
            using var source = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = sourceFile,
                Mode = SqliteOpenMode.ReadOnly,
                DefaultTimeout = 15,
                Pooling = false,
            }.ToString());
            source.Open();
            using (var check = source.CreateCommand())
            {
                check.CommandText = "PRAGMA quick_check";
                if (!string.Equals(Convert.ToString(check.ExecuteScalar(), CultureInfo.InvariantCulture), "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("备份数据库完整性检查失败");
            }
            using (var check = source.CreateCommand())
            {
                check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='schema_migrations'";
                if (Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                    throw new InvalidDataException("备份文件不包含 Lexio 数据库结构");
            }

            using (var staging = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = stagingPath, Pooling = false }.ToString()))
            {
                staging.Open();
                source.BackupDatabase(staging);
                Migrations.Apply(staging);
                CheckRequiredSchemaObjects(staging);
                CheckForeignKeys(staging);
                using var integrity = staging.CreateCommand();
                integrity.CommandText = "PRAGMA quick_check";
                if (!string.Equals(Convert.ToString(integrity.ExecuteScalar(), CultureInfo.InvariantCulture), "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("恢复后的数据库完整性检查失败");
                staging.BackupDatabase(_conn);
            }
            ExecutePragmas();
        }
        finally
        {
            TryDeleteDatabaseFiles(stagingPath);
        }
        return 0;
    });

    private void EnsureDifferentDatabase(string path)
    {
        if (string.Equals(path, _databasePath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("源数据库和目标数据库不能是同一个文件", nameof(path));
    }

    private static void CheckForeignKeys(SqliteConnection connection)
    {
        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA foreign_key_check";
        using var reader = check.ExecuteReader();
        if (reader.Read())
            throw new InvalidDataException("备份数据库包含外键错误");
    }

    private static void CheckRequiredSchemaObjects(SqliteConnection connection)
    {
        var existingTables = new Dictionary<string, string?>(StringComparer.Ordinal);
        using (var tables = connection.CreateCommand())
        {
            tables.CommandText = "SELECT name, sql FROM sqlite_master WHERE type='table'";
            using var reader = tables.ExecuteReader();
            while (reader.Read())
                existingTables[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        foreach (var (name, requiredColumns) in RequiredSchema)
        {
            if (!existingTables.TryGetValue(name, out var actualSql))
                throw new InvalidDataException($"数据库缺少 Lexio 必需的数据表：{name}");

            var existingColumns = new HashSet<string>(StringComparer.Ordinal);
            using var columns = connection.CreateCommand();
            columns.CommandText = $"PRAGMA table_info(\"{name}\")";
            using var reader = columns.ExecuteReader();
            while (reader.Read())
                existingColumns.Add(reader.GetString(1));
            if (requiredColumns.Any(column => !existingColumns.Contains(column)))
                throw new InvalidDataException($"数据库表 {name} 缺少 Lexio 必需的列");

            if (actualSql is null ||
                !string.Equals(NormalizeSchemaSql(actualSql), NormalizeSchemaSql(CanonicalTableDefinitions.Value[name]), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"数据库表 {name} 的定义与 Lexio schema 不匹配");
        }

        var existingTriggers = new Dictionary<string, (string Table, string? Sql)>(StringComparer.Ordinal);
        using (var triggers = connection.CreateCommand())
        {
            triggers.CommandText = "SELECT name, tbl_name, sql FROM sqlite_master WHERE type='trigger'";
            using var reader = triggers.ExecuteReader();
            while (reader.Read())
                existingTriggers[reader.GetString(0)] = (reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
        }
        foreach (var (name, table, expectedSql) in RequiredTriggers)
        {
            if (!existingTriggers.TryGetValue(name, out var actual) ||
                !string.Equals(actual.Table, table, StringComparison.OrdinalIgnoreCase) ||
                actual.Sql is null ||
                !string.Equals(NormalizeSchemaSql(actual.Sql), NormalizeSchemaSql(expectedSql), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"数据库缺少或包含定义错误的触发器：{name}");
        }

        foreach (var (name, table, unique, columns) in RequiredIndexes)
        {
            bool found = false;
            bool definitionMatches = false;
            using (var indexes = connection.CreateCommand())
            {
                indexes.CommandText = $"PRAGMA index_list(\"{table}\")";
                using var reader = indexes.ExecuteReader();
                while (reader.Read())
                {
                    if (!string.Equals(reader.GetString(1), name, StringComparison.Ordinal))
                        continue;

                    found = true;
                    definitionMatches = (reader.GetInt32(2) != 0) == unique && reader.GetInt32(4) == 0;
                    break;
                }
            }
            if (!found || !definitionMatches)
                throw new InvalidDataException($"数据库缺少或包含定义错误的索引：{name}");

            var actualColumns = new List<(string? Name, bool Descending, string? Collation)>();
            using (var indexInfo = connection.CreateCommand())
            {
                indexInfo.CommandText = $"PRAGMA index_xinfo(\"{name}\")";
                using var reader = indexInfo.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.GetInt32(5) == 0)
                        continue;
                    actualColumns.Add((reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.GetInt32(3) != 0, reader.IsDBNull(4) ? null : reader.GetString(4)));
                }
            }
            bool columnsMatch = actualColumns.Count == columns.Length;
            for (int i = 0; columnsMatch && i < columns.Length; i++)
                columnsMatch = string.Equals(actualColumns[i].Name, columns[i], StringComparison.Ordinal) &&
                    !actualColumns[i].Descending &&
                    string.Equals(actualColumns[i].Collation, "BINARY", StringComparison.OrdinalIgnoreCase);
            if (!columnsMatch)
                throw new InvalidDataException($"数据库缺少或包含定义错误的索引：{name}");
        }
    }

    private static Dictionary<string, string> BuildCanonicalTableDefinitions()
    {
        using var canonical = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = ":memory:",
            Pooling = false,
        }.ToString());
        canonical.Open();
        Migrations.Apply(canonical);

        var requiredTables = RequiredSchema.Select(table => table.Name).ToHashSet(StringComparer.Ordinal);
        var definitions = new Dictionary<string, string>(StringComparer.Ordinal);
        using var command = canonical.CreateCommand();
        command.CommandText = "SELECT name, sql FROM sqlite_master WHERE type='table' AND sql IS NOT NULL";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            if (requiredTables.Contains(name))
                definitions[name] = reader.GetString(1);
        }

        if (definitions.Count != requiredTables.Count)
            throw new InvalidOperationException("Lexio migrations do not define every required table");
        return definitions;
    }

    private static void TryDeleteDatabaseFiles(string path)
    {
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
        {
            try { if (File.Exists(file)) File.Delete(file); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string NormalizeSchemaSql(string sql) =>
        string.Join(" ", sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public ValueTask DisposeAsync()
    {
        lock (_lifetimeLock)
        {
            if (_disposeTask is null)
            {
                _disposeStarted = true;
                var operationsDrained = _pendingOperations == 0
                    ? Task.CompletedTask
                    : (_operationsDrained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
                _disposeTask = DisposeCoreAsync(operationsDrained);
            }
            return new ValueTask(_disposeTask);
        }
    }

    private async Task RunAsync(Action work)
    {
        RegisterOperation();
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try { await Task.Run(work).ConfigureAwait(false); }
            finally { _gate.Release(); }
        }
        finally
        {
            CompleteOperation();
        }
    }

    private async Task<T> RunAsync<T>(Func<T> work)
    {
        RegisterOperation();
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try { return await Task.Run(work).ConfigureAwait(false); }
            finally { _gate.Release(); }
        }
        finally
        {
            CompleteOperation();
        }
    }

    private async Task DisposeCoreAsync(Task operationsDrained)
    {
        await operationsDrained.ConfigureAwait(false);
        try { await _conn.DisposeAsync().ConfigureAwait(false); }
        finally { _gate.Dispose(); }
    }

    private void RegisterOperation()
    {
        lock (_lifetimeLock)
        {
            if (_disposeStarted)
                throw new ObjectDisposedException(nameof(AppDatabase));
            _pendingOperations++;
        }
    }

    private void CompleteOperation()
    {
        TaskCompletionSource? operationsDrained = null;
        lock (_lifetimeLock)
        {
            if (--_pendingOperations == 0)
            {
                operationsDrained = _operationsDrained;
                _operationsDrained = null;
            }
        }
        operationsDrained?.TrySetResult();
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
                    "INSERT INTO words(lemma, ipa_us, ipa_uk, frequency_rank, cefr, details_json) VALUES(@l, @us, @uk, @fr, @ce, @details); SELECT last_insert_rowid();",
                    ("@l", word.Lemma), ("@us", word.IpaUs), ("@uk", word.IpaUk), ("@fr", word.FrequencyRank),
                    ("@ce", word.Cefr), ("@details", SerializeWordDetails(word.Details)));
                ins.Transaction = tx;
                wordId = Convert.ToInt64(ins.ExecuteScalar()!, CultureInfo.InvariantCulture);
            }
            else
            {
                wordId = Convert.ToInt64(existing, CultureInfo.InvariantCulture);
                using var upd = Cmd(
                    "UPDATE words SET ipa_us=@us, ipa_uk=@uk, frequency_rank=@fr, cefr=@ce, details_json=COALESCE(@details, details_json) WHERE id=@id",
                    ("@us", word.IpaUs), ("@uk", word.IpaUk), ("@fr", word.FrequencyRank), ("@ce", word.Cefr),
                    ("@details", SerializeWordDetails(word.Details)), ("@id", wordId));
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
        using var cmd = Cmd("SELECT id, lemma, ipa_us, ipa_uk, frequency_rank, cefr, details_json FROM words WHERE id = @id", ("@id", wordId));
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
        [])
    {
        Details = DeserializeWordDetails(reader.IsDBNull(6) ? null : reader.GetString(6)),
    };

    internal static WordDetails DeserializeWordDetails(string? json) => string.IsNullOrWhiteSpace(json)
        ? WordDetails.Empty
        : JsonSerializer.Deserialize<WordDetails>(json) ?? WordDetails.Empty;

    internal static string? SerializeWordDetails(WordDetails details) => details.HasContent
        ? JsonSerializer.Serialize(details)
        : null;

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

    /// <summary>Prefix phrase search over FTS5, optionally scoped to a deck, transparently falling back to LIKE.</summary>
    public Task<List<WordEntry>> SearchWordsAsync(string query, int limit = 30, long? deckId = null) => RunAsync(() =>
    {
        var sanitized = SanitizeFtsQuery(query);
        List<long> ids = [];
        if (string.IsNullOrEmpty(sanitized) && deckId.HasValue)
        {
            using var cmd = Cmd(
                "SELECT word_id FROM deck_words WHERE deck_id = @d ORDER BY position LIMIT @n",
                ("@d", deckId.Value), ("@n", limit));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                ids.Add(reader.GetInt64(0));
        }
        else if (!string.IsNullOrEmpty(sanitized))
        {
            try
            {
                var sql = "SELECT w.id FROM words_fts f JOIN words w ON w.id = f.rowid WHERE words_fts MATCH @m" +
                    (deckId.HasValue ? " AND EXISTS (SELECT 1 FROM deck_words dw WHERE dw.deck_id = @d AND dw.word_id = w.id)" : "") +
                    " ORDER BY rank LIMIT @n";
                using var cmd = deckId.HasValue
                    ? Cmd(sql, ("@m", sanitized), ("@d", deckId.Value), ("@n", limit))
                    : Cmd(sql, ("@m", sanitized), ("@n", limit));
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
            var sql = "SELECT id FROM words WHERE lemma LIKE @p || '%'" +
                (deckId.HasValue ? " AND EXISTS (SELECT 1 FROM deck_words dw WHERE dw.deck_id = @d AND dw.word_id = words.id)" : "") +
                " ORDER BY frequency_rank IS NULL, frequency_rank LIMIT @n";
            using var cmd = deckId.HasValue
                ? Cmd(sql, ("@p", query.Trim()), ("@d", deckId.Value), ("@n", limit))
                : Cmd(sql, ("@p", query.Trim()), ("@n", limit));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                ids.Add(reader.GetInt64(0));
        }
        return LoadWordsWithSensesCore(ids);
    });

    internal static string? SanitizeFtsQuery(string input)
    {
        var tokens = new List<string>();
        var token = new StringBuilder();
        foreach (char character in input)
        {
            if (char.IsLetterOrDigit(character))
                token.Append(character);
            else if (token.Length > 0)
            {
                tokens.Add(token.ToString());
                token.Clear();
            }
        }
        if (token.Length > 0)
            tokens.Add(token.ToString());
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
        using (var cmd = Cmd($"SELECT id, lemma, ipa_us, ipa_uk, frequency_rank, cefr, details_json FROM words WHERE id IN ({placeholders})", args))
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
