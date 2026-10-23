using Lexico.Core.Data;
using Lexico.Core.Dictionary;
using Lexico.Core.Domain;
using Lexico.Core.Scheduling;
using Lexico.Core.Study;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Lexico.Core.Tests;

public sealed class DataReliabilityTests : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lexio-data-" + Guid.NewGuid().ToString("N"));
    private readonly AppDatabase _db;

    public DataReliabilityTests()
    {
        _db = new AppDatabase(Path.Combine(_directory, "main.db"));
        _db.MigrateAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task BackupCanBeRestoredAndInvalidBackupLeavesCurrentDataUntouched()
    {
        var originalWordId = await _db.UpsertWordAsync(new(0, "preserved", null, null, null, null,
            [new(0, "n.", "原释义", null, null, null)]));
        var backup = Path.Combine(_directory, "backup.db");
        await File.WriteAllTextAsync(backup, "old incomplete backup");
        await _db.BackupToAsync(backup);
        await _db.UpsertWordAsync(new(originalWordId, "preserved", null, null, null, null,
            [new(0, "n.", "修改后的释义", null, null, null)]));

        await _db.RestoreFromAsync(backup);

        var restored = Assert.Single(await _db.SearchWordsAsync("preserved", 5));
        Assert.Equal("原释义", restored.Senses.Single().ZhGloss);

        var invalid = Path.Combine(_directory, "invalid.db");
        await File.WriteAllTextAsync(invalid, "not sqlite");
        await Assert.ThrowsAsync<SqliteException>(() => _db.RestoreFromAsync(invalid));
        restored = Assert.Single(await _db.SearchWordsAsync("preserved", 5));
        Assert.Equal("原释义", restored.Senses.Single().ZhGloss);
    }

    [Fact]
    public async Task RestoreUpgradesVersionEightBackupAndPreservesArticleProgress()
    {
        var legacyPath = Path.Combine(_directory, "legacy-v8.db");
        await using (var legacy = new AppDatabase(legacyPath))
            await legacy.MigrateAsync();

        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = legacyPath,
            Pooling = false,
        }.ToString()))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                ALTER TABLE words DROP COLUMN details_json;
                ALTER TABLE articles DROP COLUMN title_translation;
                DELETE FROM schema_migrations WHERE version=9;
                INSERT INTO articles(id, title, created_at, updated_at)
                    VALUES(41, 'Legacy article', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
                INSERT INTO article_sentences(id, article_id, position, text, translation)
                    VALUES(91, 41, 0, 'A retained sentence.', '保留的句子。');
                INSERT INTO article_progress(article_id, current_sentence, mode, is_complete, updated_at)
                    VALUES(41, 0, 1, 0, '2026-01-02T00:00:00Z');
                """;
            await seed.ExecuteNonQueryAsync();
        }

        await _db.RestoreFromAsync(legacyPath);

        var article = await _db.GetArticleAsync(41);
        Assert.NotNull(article);
        Assert.Equal("Legacy article", article!.Summary.Title);
        Assert.Null(article.Summary.TitleTranslation);
        Assert.Equal("A retained sentence.", Assert.Single(article.Sentences).Text);
        Assert.Equal("保留的句子。", article.Sentences[0].Translation);
        Assert.Equal(0, article.Summary.CurrentSentence);
        Assert.Equal(ArticlePracticeMode.Dictation, article.Summary.Mode);

        await using var schema = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "main.db"),
            Pooling = false,
        }.ToString());
        await schema.OpenAsync();
        await using var command = schema.CreateCommand();
        command.CommandText = """
            SELECT (SELECT MAX(version) FROM schema_migrations),
                   (SELECT COUNT(*) FROM pragma_table_info('articles') WHERE name='title_translation'),
                   (SELECT COUNT(*) FROM pragma_table_info('words') WHERE name='details_json')
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(9, reader.GetInt32(0));
        Assert.Equal(1L, reader.GetInt64(1));
        Assert.Equal(1L, reader.GetInt64(2));
    }

    [Fact]
    public async Task BackupValidationKeepsThePreviousGoodSnapshotWhenSourceSchemaIsInvalid()
    {
        await _db.UpsertWordAsync(new(0, "backup-snapshot", null, null, null, null, []));
        var backupPath = Path.Combine(_directory, "good-snapshot.db");
        await _db.BackupToAsync(backupPath);

        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "main.db"),
            Pooling = false,
        }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TRIGGER review_logs_no_update";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => _db.BackupToAsync(backupPath));

        await using var preserved = await AppDatabase.OpenWithRecoveryAsync(backupPath, backupPath + ".fallback");
        Assert.Single(await preserved.SearchWordsAsync("backup-snapshot", 5));
    }

    [Fact]
    public async Task OpenWithRecoveryRestoresValidatedBackupAndPreservesCorruptPrimary()
    {
        var mainPath = Path.Combine(_directory, "recover.db");
        var backupPath = mainPath + ".backup";
        long expectedWordId;
        await using (var source = new AppDatabase(mainPath))
        {
            await source.MigrateAsync();
            expectedWordId = await source.UpsertWordAsync(new(0, "recovered", null, null, null, null,
                [new(0, "v.", "恢复", null, null, null)]));
            await source.BackupToAsync(backupPath);
        }

        byte[] corruptContents = [0x4c, 0x65, 0x78, 0x69, 0x6f, 0xff];
        await File.WriteAllBytesAsync(mainPath, corruptContents);

        await using var recovered = await AppDatabase.OpenWithRecoveryAsync(mainPath, backupPath);

        var word = await recovered.GetWordAsync(expectedWordId);
        Assert.Equal("recovered", word!.Lemma);
        Assert.Equal("恢复", word.Senses.Single().ZhGloss);
        Assert.Contains(Directory.GetFiles(_directory, "recover.db.corrupt-*"), path => File.Exists(path));
        Assert.Equal(1, await recovered.CountWordsAsync());
    }

    [Fact]
    public async Task OpenWithRecoveryWithoutBackupPreservesCorruptPrimaryAndExplainsFailure()
    {
        var mainPath = Path.Combine(_directory, "no-backup.db");
        byte[] corruptContents = [0x4c, 0x65, 0x78, 0x69, 0x6f, 0xff];
        await File.WriteAllBytesAsync(mainPath, corruptContents);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => AppDatabase.OpenWithRecoveryAsync(mainPath, mainPath + ".backup"));

        Assert.Contains("备份", error.Message);
        Assert.Equal(corruptContents, await File.ReadAllBytesAsync(mainPath));

        var invalidBackup = mainPath + ".backup";
        await File.WriteAllTextAsync(invalidBackup, "not a SQLite backup");
        var invalidBackupError = await Assert.ThrowsAsync<InvalidDataException>(
            () => AppDatabase.OpenWithRecoveryAsync(mainPath, invalidBackup));
        Assert.Contains("自动备份无效", invalidBackupError.Message);
        Assert.Equal(corruptContents, await File.ReadAllBytesAsync(mainPath));
    }

    [Fact]
    public async Task RestoreRejectsValidSqliteFileWithoutLexioTables()
    {
        await _db.UpsertWordAsync(new(0, "restore-guard", null, null, null, null, []));
        var incomplete = Path.Combine(_directory, "incomplete.db");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = incomplete,
        }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL);
                INSERT INTO schema_migrations(version, applied_at) VALUES(1, 'fixture'), (2, 'fixture'),
                    (3, 'fixture'), (4, 'fixture'), (5, 'fixture'), (6, 'fixture');
                CREATE TABLE words(id INTEGER PRIMARY KEY, lemma TEXT NOT NULL);
                CREATE TABLE cards(id INTEGER PRIMARY KEY, word_id INTEGER NOT NULL, deck_id INTEGER NOT NULL,
                    due TEXT NOT NULL, created_at TEXT NOT NULL);
                CREATE TABLE review_logs(id INTEGER PRIMARY KEY, card_id INTEGER NOT NULL, reviewed_at TEXT NOT NULL,
                    rating INTEGER NOT NULL, state_before INTEGER NOT NULL, engine_version TEXT NOT NULL,
                    next_due TEXT NOT NULL, next_interval_days REAL NOT NULL);
                CREATE TABLE sessions(id INTEGER PRIMARY KEY, started_at TEXT NOT NULL, intensity INTEGER NOT NULL);
                """;
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => _db.RestoreFromAsync(incomplete));

        Assert.Single(await _db.SearchWordsAsync("restore-guard", 5));
    }

    [Fact]
    public async Task RestoreRejectsDatabaseMissingRequiredTableColumns()
    {
        var wordId = await _db.UpsertWordAsync(new(0, "schema-columns", null, null, null, null, []));
        var incomplete = Path.Combine(_directory, "missing-column.db");
        await _db.BackupToAsync(incomplete);
        await _db.UpsertWordAsync(new(wordId, "schema-columns", null, null, null, null,
            [new(0, "n.", "保留", null, null, null)]));
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = incomplete,
            Pooling = false,
        }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE settings DROP COLUMN value";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => _db.RestoreFromAsync(incomplete));

        Assert.Equal("保留", (await _db.GetWordAsync(wordId))!.Senses.Single().ZhGloss);
    }

    [Fact]
    public async Task RestoreRejectsDatabaseMissingLemmaUniquenessIndexAndPreservesCurrentData()
    {
        var wordId = await _db.UpsertWordAsync(new(0, "index-guard", null, null, null, null,
            [new(0, "n.", "当前数据", null, null, null)]));
        var incomplete = Path.Combine(_directory, "missing-lemma-index.db");
        await _db.BackupToAsync(incomplete);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = incomplete,
            Pooling = false,
        }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP INDEX idx_words_lemma";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => _db.RestoreFromAsync(incomplete));

        Assert.Equal("当前数据", (await _db.GetWordAsync(wordId))!.Senses.Single().ZhGloss);
    }

    [Fact]
    public async Task RestoreRejectsLemmaIndexWithTheRightNameButWrongDefinition()
    {
        var wordId = await _db.UpsertWordAsync(new(0, "index-definition-guard", null, null, null, null,
            [new(0, "n.", "当前数据", null, null, null)]));
        var incomplete = Path.Combine(_directory, "wrong-lemma-index.db");
        await _db.BackupToAsync(incomplete);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = incomplete,
            Pooling = false,
        }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP INDEX idx_words_lemma; CREATE INDEX idx_words_lemma ON words(id)";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => _db.RestoreFromAsync(incomplete));

        Assert.Equal("当前数据", (await _db.GetWordAsync(wordId))!.Senses.Single().ZhGloss);
    }

    [Fact]
    public async Task RestoreRejectsLemmaIndexWithMismatchedCollation()
    {
        var incomplete = Path.Combine(_directory, "wrong-lemma-collation.db");
        await _db.BackupToAsync(incomplete);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = incomplete,
            Pooling = false,
        }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP INDEX idx_words_lemma; CREATE UNIQUE INDEX idx_words_lemma ON words(lemma COLLATE NOCASE)";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => _db.RestoreFromAsync(incomplete));
    }

    [Fact]
    public async Task RestoreRejectsDatabaseMissingImmutableHistoryTriggers()
    {
        var incomplete = Path.Combine(_directory, "missing-history-trigger.db");
        await _db.BackupToAsync(incomplete);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = incomplete,
            Pooling = false,
        }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TRIGGER review_logs_no_update";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => _db.RestoreFromAsync(incomplete));
        Assert.Equal(0, await _db.CountWordsAsync());
    }

    [Theory]
    [InlineData("words_ai", "DROP TRIGGER words_ai; CREATE TRIGGER words_ai AFTER INSERT ON words BEGIN SELECT 1; END")]
    [InlineData("words_ad", "DROP TRIGGER words_ad; CREATE TRIGGER words_ad AFTER DELETE ON words BEGIN SELECT 1; END")]
    [InlineData("words_au", "DROP TRIGGER words_au; CREATE TRIGGER words_au AFTER UPDATE ON words BEGIN SELECT 1; END")]
    [InlineData("review_logs_no_update", "DROP TRIGGER review_logs_no_update; CREATE TRIGGER review_logs_no_update AFTER UPDATE ON review_logs BEGIN SELECT 1; END")]
    [InlineData("review_logs_no_delete", "DROP TRIGGER review_logs_no_delete; CREATE TRIGGER review_logs_no_delete AFTER DELETE ON review_logs BEGIN SELECT 1; END")]
    public async Task RestoreRejectsRequiredTriggerWithTheRightNameButWrongDefinition(string triggerName, string replacementSql)
    {
        var wordId = await _db.UpsertWordAsync(new(0, "trigger-definition-guard", null, null, null, null,
            [new(0, "n.", "当前数据", null, null, null)]));
        var incomplete = Path.Combine(_directory, $"wrong-{triggerName}.db");
        await _db.BackupToAsync(incomplete);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = incomplete,
            Pooling = false,
        }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = replacementSql;
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => _db.RestoreFromAsync(incomplete));

        Assert.Equal("当前数据", (await _db.GetWordAsync(wordId))!.Senses.Single().ZhGloss);
    }

    [Fact]
    public async Task RestoreRejectsDeckWordsTableMissingItsForeignKeysAndPreservesCurrentData()
    {
        var deckId = await _db.EnsureDeckAsync("schema-foreign-key-guard", null, null, builtIn: false);
        var wordId = await _db.UpsertWordAsync(new(0, "schema-foreign-key-guard", null, null, null, null,
            [new(0, "n.", "当前数据", null, null, null)]));
        await _db.AddWordToDeckAsync(deckId, wordId, 0);
        var incomplete = Path.Combine(_directory, "missing-deck-word-foreign-keys.db");
        await _db.BackupToAsync(incomplete);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = incomplete,
            Pooling = false,
        }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA foreign_keys=OFF;
                ALTER TABLE deck_words RENAME TO deck_words_old;
                CREATE TABLE deck_words(
                    deck_id INTEGER NOT NULL,
                    word_id INTEGER NOT NULL,
                    position INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY(deck_id, word_id)
                );
                INSERT INTO deck_words SELECT deck_id, word_id, position FROM deck_words_old;
                DROP TABLE deck_words_old;
                CREATE INDEX idx_deck_words_word ON deck_words(word_id);
                """;
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => _db.RestoreFromAsync(incomplete));

        Assert.Equal("当前数据", (await _db.GetWordAsync(wordId))!.Senses.Single().ZhGloss);
        Assert.Equal(1, await _db.CountWordsAsync());
    }

    [Fact]
    public async Task RestoreRejectsDailyPlansTableMissingUniqueConstraint()
    {
        await _db.SaveDailyPlanAsync("2026-10-18", Intensity.Normal, reviewCount: 2, newCount: 1, estimatedMinutes: 5);
        var incomplete = Path.Combine(_directory, "missing-daily-plan-unique-constraint.db");
        await _db.BackupToAsync(incomplete);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = incomplete,
            Pooling = false,
        }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE daily_plans RENAME TO daily_plans_old;
                CREATE TABLE daily_plans(
                    id INTEGER PRIMARY KEY,
                    date TEXT NOT NULL,
                    intensity INTEGER NOT NULL,
                    review_count INTEGER NOT NULL,
                    new_count INTEGER NOT NULL,
                    estimated_minutes REAL NOT NULL,
                    created_at TEXT NOT NULL
                );
                INSERT INTO daily_plans SELECT id, date, intensity, review_count, new_count, estimated_minutes, created_at
                    FROM daily_plans_old;
                DROP TABLE daily_plans_old;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => _db.RestoreFromAsync(incomplete));
    }

    [Fact]
    public async Task ExportIncludesImmutableReviewAndShadowTrainingData()
    {
        await new DictionaryService(_db).EnsureStarterDeckAsync();
        var study = new StudyService(_db, new FsrsCoreEngine(enableFuzzing: false));
        await study.StartAsync(Intensity.Normal);
        var item = study.PeekNext()!;
        await study.AnswerAsync(item, Grade.Good, new StudyAnswerContext(HintUsed: true));
        await study.FinishAsync();

        var exportPath = Path.Combine(_directory, "history.csv");
        await _db.ExportReviewHistoryCsvAsync(exportPath);

        var lines = await File.ReadAllLinesAsync(exportPath);
        var columns = DictionaryService.ParseCsvLine(lines[0]);
        var row = DictionaryService.ParseCsvLine(lines[1]);
        Assert.Equal(1, lines.Length - 1);
        Assert.Equal("1", row[columns.IndexOf("hint_used")]);
        Assert.Equal("1", row[columns.IndexOf("model_version")]);
        Assert.Contains("\"FsrsProbability\"", row[columns.IndexOf("features_json")]);
        Assert.Equal("0", row[columns.IndexOf("raw_recall")]); // new cards have no FSRS recall history
    }

    [Fact]
    public async Task ReviewLogsRejectUpdatesAndDeletes()
    {
        var deckId = await _db.EnsureDeckAsync("Immutable history", null, null, builtIn: false);
        var wordId = await _db.UpsertWordAsync(new(0, "immutable", null, null, null, null, []));
        await _db.AddWordToDeckAsync(deckId, wordId, 0);
        await _db.EnsureCardsForDeckAsync(deckId);
        var card = Assert.Single(await _db.GetNewCardsAsync(1, deckId));
        var updated = card with { Reps = 1, LastReviewUtc = DateTime.UtcNow };
        var log = new ReviewLogRow(0, card.Id, DateTime.UtcNow, Grade.Good, card.State, card.Step,
            card.Stability, card.Difficulty, 0, 0, 0, 0, false, null, "test", updated.DueUtc, 0);
        await _db.AnswerCardAsync(updated, log);

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "main.db"),
        }.ToString());
        await connection.OpenAsync();
        await using var update = connection.CreateCommand();
        update.CommandText = "UPDATE review_logs SET rating=4 WHERE card_id=$card";
        update.Parameters.AddWithValue("$card", card.Id);
        await Assert.ThrowsAsync<SqliteException>(() => update.ExecuteNonQueryAsync());
        await using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM review_logs WHERE card_id=$card";
        delete.Parameters.AddWithValue("$card", card.Id);
        await Assert.ThrowsAsync<SqliteException>(() => delete.ExecuteNonQueryAsync());
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
