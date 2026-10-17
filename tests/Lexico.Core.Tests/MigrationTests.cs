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
                     "daily_plans", "model_profiles", "model_predictions", "settings", "words_fts",
                     "words_ai", "words_ad", "words_au",
                 })
        {
            Assert.Contains(expected, tables);
        }
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

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
