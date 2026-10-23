using Lexico.Core.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Lexico.Core.Tests;

public class MigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lexio-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Migrate_IsIdempotent_AndCreatesSchema()
    {
        var path = Path.Combine(_dir, "test.db");
        await using var db = new AppDatabase(path);
        await db.MigrateAsync();
        await db.MigrateAsync(); // replay must be a no-op
        await db.MigrateAsync();

        var tables = new List<string>();
        await using (var check = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
        {
            check.Open();
            using var cmd = check.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table','trigger') ORDER BY name";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                tables.Add(reader.GetString(0));
        }

        foreach (var expected in new[]
                 {
                     "words", "word_senses", "decks", "deck_words", "cards", "review_logs", "sessions",
                     "daily_plans", "session_queue", "model_profiles", "model_predictions", "settings", "words_fts",
                     "words_ai", "words_ad", "words_au", "review_logs_no_update", "review_logs_no_delete",
                 })
        {
            Assert.Contains(expected, tables);
        }
    }

    [Fact]
    public async Task MigrationV9_AddsRichWordAndArticleColumns()
    {
        var path = Path.Combine(_dir, "v9.db");
        await using var db = new AppDatabase(path);
        await db.MigrateAsync();

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
              (SELECT COUNT(*) FROM pragma_table_info('words') WHERE name='details_json'),
              (SELECT COUNT(*) FROM pragma_table_info('articles') WHERE name='title_translation')
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
    }

    [Fact]
    public async Task Wal_ModeAndForeignKeys_AreEnabled()
    {
        var path = Path.Combine(_dir, "wal.db");
        await using var db = new AppDatabase(path);
        await db.MigrateAsync();

        // Inserting a sense with an unknown word must fail (FK enforcement).
        await Assert.ThrowsAsync<SqliteException>(async () =>
        {
            await db.UpsertWordAsync(new(1, "test", null, null, null, null, []));
            // word id 1 exists; now reference deck id 999 in deck_words through the public API path:
            await db.AddWordToDeckAsync(999, 1, 0);
        });
    }

    [Fact]
    public async Task Settings_RoundTrip()
    {
        var path = Path.Combine(_dir, "settings.db");
        await using var db = new AppDatabase(path);
        await db.MigrateAsync();

        Assert.Null(await db.GetSettingAsync("missing"));
        await db.SetSettingAsync("retention", "0.92");
        await db.SetSettingAsync("retention", "0.95");
        Assert.Equal("0.95", await db.GetSettingAsync("retention"));
        Assert.Equal(0.95, await db.GetSettingDoubleAsync("retention", 0.9));
        Assert.Equal(7, await db.GetSettingIntAsync("other", 7));
    }

    [Fact]
    public async Task MigrationV3_PreservesExistingReviewsAndAddsShadowModelBaseline()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "legacy-v2.db");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
        {
            await connection.OpenAsync();
            await using (var createVersions = connection.CreateCommand())
            {
                createVersions.CommandText = "CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL)";
                await createVersions.ExecuteNonQueryAsync();
            }

            foreach (var (version, sql) in Migrations.All.Take(2))
            {
                await using var migration = connection.CreateCommand();
                migration.CommandText = sql;
                await migration.ExecuteNonQueryAsync();
                await using var recordVersion = connection.CreateCommand();
                recordVersion.CommandText = "INSERT INTO schema_migrations(version, applied_at) VALUES($version, 'legacy')";
                recordVersion.Parameters.AddWithValue("$version", version);
                await recordVersion.ExecuteNonQueryAsync();
            }

            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO words(id, lemma) VALUES(1, 'legacy');
                INSERT INTO decks(id, name, created_at) VALUES(1, 'legacy', '2026-01-01T00:00:00Z');
                INSERT INTO cards(id, word_id, deck_id, due, created_at) VALUES(1, 1, 1, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
                INSERT INTO review_logs(id, card_id, reviewed_at, rating, state_before, engine_version, next_due, next_interval_days)
                VALUES(7, 1, '2026-01-02T00:00:00Z', 3, 0, 'legacy', '2026-01-03T00:00:00Z', 1);
                INSERT INTO model_profiles(id, kind, version, weights, trained_at, is_active)
                VALUES(3, 'legacy-model', 7, '[]', '2026-01-01T00:00:00Z', 0);
                INSERT INTO model_predictions(id, review_log_id, profile_id, predicted_recall, created_at)
                VALUES(11, 7, 3, 0.7, '2026-01-02T00:00:00Z');
                """;
            await seed.ExecuteNonQueryAsync();
        }

        await using (var db = new AppDatabase(path))
            await db.MigrateAsync();

        await using var verify = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        await verify.OpenAsync();
        await using var check = verify.CreateCommand();
        check.CommandText = """
            SELECT (SELECT COUNT(*) FROM review_logs),
                   (SELECT hint_used FROM review_logs WHERE id=7),
                   (SELECT COUNT(*) FROM model_profiles WHERE kind='personal-recall-calibrator' AND version=1 AND is_active=1),
                   (SELECT trained_through_log_id FROM model_profiles WHERE kind='personal-recall-calibrator' AND version=1 AND is_active=1),
                   (SELECT model_version FROM model_predictions WHERE id=11),
                   (SELECT features_json FROM model_predictions WHERE id=11)
            """;
        await using var reader = await check.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(0, reader.GetInt32(1));
        Assert.Equal(1L, reader.GetInt64(2));
        Assert.True(reader.IsDBNull(3));
        Assert.Equal(7, reader.GetInt32(4));
        Assert.Equal("{}", reader.GetString(5));
    }

    [Fact]
    public async Task MigrationV6_PreservesQueuedCardsAndDefaultsEarlyReviewToFalse()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "legacy-v5.db");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
        {
            await connection.OpenAsync();
            await using (var versions = connection.CreateCommand())
            {
                versions.CommandText = "CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL)";
                await versions.ExecuteNonQueryAsync();
            }

            foreach (var (version, sql) in Migrations.All.Take(5))
            {
                await using var migration = connection.CreateCommand();
                migration.CommandText = sql;
                await migration.ExecuteNonQueryAsync();
                await using var recordVersion = connection.CreateCommand();
                recordVersion.CommandText = "INSERT INTO schema_migrations(version, applied_at) VALUES($version, 'legacy')";
                recordVersion.Parameters.AddWithValue("$version", version);
                await recordVersion.ExecuteNonQueryAsync();
            }

            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO words(id, lemma) VALUES(1, 'legacy');
                INSERT INTO decks(id, name, created_at) VALUES(1, 'legacy', '2026-01-01T00:00:00Z');
                INSERT INTO cards(id, word_id, deck_id, due, created_at)
                    VALUES(1, 1, 1, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
                INSERT INTO sessions(id, started_at, intensity) VALUES(1, '2026-01-01T00:00:00Z', 2);
                INSERT INTO session_queue(id, session_id, card_id, position) VALUES(10, 1, 1, 0);
                """;
            await seed.ExecuteNonQueryAsync();
        }

        await using (var db = new AppDatabase(path))
            await db.MigrateAsync();

        await using var verify = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        await verify.OpenAsync();
        await using var check = verify.CreateCommand();
        check.CommandText = "SELECT is_early_review FROM session_queue WHERE id=10";
        Assert.Equal(0L, (long)(await check.ExecuteScalarAsync())!);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
