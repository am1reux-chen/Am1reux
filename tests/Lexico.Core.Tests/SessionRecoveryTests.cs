using Lexico.Core.Data;
using Lexico.Core.Dictionary;
using Lexico.Core.Domain;
using Lexico.Core.Scheduling;
using Lexico.Core.Study;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Lexico.Core.Tests;

public sealed class SessionRecoveryTests : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lexio-session-recovery-" + Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;
    private AppDatabase? _db;

    public SessionRecoveryTests()
    {
        _databasePath = Path.Combine(_directory, "study.db");
        _db = new AppDatabase(_databasePath);
        _db.MigrateAsync().GetAwaiter().GetResult();
        new DictionaryService(_db).EnsureStarterDeckAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task InterruptedSessionResumesPersistedQueueAndCommittedRelearningStep()
    {
        var initialDb = _db!;
        var firstProcess = new StudyService(initialDb, new FsrsCoreEngine(enableFuzzing: false));
        var originalPlan = await firstProcess.StartAsync(Intensity.Normal);
        var item = firstProcess.PeekNext()!;
        await firstProcess.AnswerAsync(item, Grade.Again);
        var cardVisibleBeforeExit = firstProcess.PeekNext()!;
        firstProcess.RevealCard(cardVisibleBeforeExit.CardId);
        await firstProcess.PauseAsync();
        Assert.True(firstProcess.IsPaused);
        int remaining = firstProcess.Remaining;
        await initialDb.DisposeAsync(); // simulate exit before FinishAsync
        _db = null;

        _db = new AppDatabase(_databasePath);
        await _db.MigrateAsync();
        var restarted = new StudyService(_db, new FsrsCoreEngine(enableFuzzing: false));
        int resumed = await restarted.RecoverInterruptedSessionsAsync();

        Assert.Equal(1, resumed);
        Assert.True(restarted.IsActive);
        Assert.False(restarted.IsPaused);
        Assert.Equal(1, restarted.CompletedReviews);
        Assert.Equal(remaining, restarted.Remaining);
        Assert.Equal(originalPlan.TotalCards, restarted.CurrentPlan!.TotalCards);
        var card = await _db.GetCardAsync(item.CardId);
        Assert.Equal(1, card!.Reps);
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM review_logs WHERE card_id=$card", item.CardId));
        Assert.Equal(1L, await CountAsync("""
            SELECT COUNT(*) FROM review_logs
            WHERE session_id=(SELECT id FROM sessions WHERE ended_at IS NULL) AND rating=1
            """));
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM sessions WHERE ended_at IS NULL"));
        Assert.Equal(1L, await CountAsync("""
            SELECT COUNT(*) FROM session_queue
            WHERE session_id=(SELECT id FROM sessions WHERE ended_at IS NULL) AND card_id=$card
            """, item.CardId));
        Assert.False(restarted.IsCardRevealed(cardVisibleBeforeExit.CardId));
        Assert.Equal(cardVisibleBeforeExit.CardId, restarted.PeekNext()?.CardId);
        Assert.Equal(0, await restarted.RecoverInterruptedSessionsAsync());
        var summary = await restarted.FinishAsync();
        Assert.Equal(1, summary.Reviews);
        Assert.Equal(1, summary.Again);
    }

    [Fact]
    public async Task InterruptedHeavySessionResumesEarlyReviewWithoutChangingItsDueDate()
    {
        var dueUtc = DateTime.UtcNow.AddDays(1);
        long cardId;
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString()))
        {
            await connection.OpenAsync();
            await using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE cards SET state=1, step=NULL, stability=5, difficulty=5, due=$due,
                                 last_review=$last, reps=3
                WHERE id=(SELECT MIN(id) FROM cards)
                RETURNING id
                """;
            update.Parameters.AddWithValue("$due", dueUtc.ToString("O"));
            update.Parameters.AddWithValue("$last", DateTime.UtcNow.AddDays(-2).ToString("O"));
            cardId = (long)(await update.ExecuteScalarAsync())!;
        }

        var initialDb = _db!;
        var firstProcess = new StudyService(initialDb, new FsrsCoreEngine(enableFuzzing: false));
        var plan = await firstProcess.StartAsync(Intensity.Heavy);
        Assert.True(plan.TotalCards > 0);
        await initialDb.DisposeAsync(); // simulate process exit before finish or presenting the early card
        _db = null;

        _db = new AppDatabase(_databasePath);
        await _db.MigrateAsync();
        var restarted = new StudyService(_db, new FsrsCoreEngine(enableFuzzing: false));
        Assert.Equal(1, await restarted.RecoverInterruptedSessionsAsync());

        var next = restarted.PeekNext();
        Assert.Equal(cardId, next?.CardId);
        Assert.True(next!.DueUtc > DateTime.UtcNow.AddHours(12));
        Assert.True(restarted.NextDueUtc() <= DateTime.UtcNow.AddSeconds(2));

        var card = await _db.GetCardAsync(cardId);
        Assert.Equal(dueUtc.ToString("O"), card!.DueUtc.ToString("O"));
        await restarted.FinishAsync();
    }

    private async Task<long> CountAsync(string sql, long? cardId = null)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (cardId is not null)
            command.Parameters.AddWithValue("$card", cardId.Value);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    public async ValueTask DisposeAsync()
    {
        if (_db is not null)
            await _db.DisposeAsync();
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
