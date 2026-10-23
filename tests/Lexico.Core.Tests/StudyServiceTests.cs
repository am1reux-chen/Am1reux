using Lexico.Core.Dictionary;
using Lexico.Core.Domain;
using Lexico.Core.Study;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Lexico.Core.Tests;

public sealed class StudyServiceTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lexio-study-" + Guid.NewGuid().ToString("N"));
    private readonly Data.AppDatabase _db;
    private readonly StudyService _study;

    public StudyServiceTests()
    {
        _db = new Data.AppDatabase(Path.Combine(_dir, "study.db"));
        _db.MigrateAsync().GetAwaiter().GetResult();
        new DictionaryService(_db).EnsureStarterDeckAsync().GetAwaiter().GetResult();
        _study = new StudyService(_db, new Scheduling.FsrsCoreEngine(enableFuzzing: false));
    }

    [Fact]
    public async Task Start_BuildsQueue_FromStarterDeck()
    {
        var plan = await _study.StartAsync(Intensity.Normal);

        Assert.True(plan.TotalCards > 0, "starter deck must yield cards");
        Assert.True(plan.NewCount > 0);
        var next = _study.PeekNext();
        Assert.NotNull(next);
        Assert.False(string.IsNullOrEmpty(next!.Lemma));
        Assert.NotNull(next.Senses as IEnumerable<WordSense>);
        await _study.FinishAsync();
    }

    [Fact]
    public async Task BuildPlansPreviewMatchesTheQueueStartedForThatIntensity()
    {
        var plans = await _study.BuildPlansAsync(DateTime.UtcNow);
        var normal = plans[Intensity.Normal];

        var session = await _study.StartAsync(Intensity.Normal);

        Assert.Equal(normal.ReviewCount + normal.EarlyReviewCount + normal.NewCount, session.TotalCards);
        Assert.Equal(normal.NewCount, session.NewCount);
        Assert.Equal(normal.EstimatedMinutes, session.EstimatedMinutes);
        await _study.FinishAsync();
    }

    [Fact]
    public async Task ConcurrentStartCallsCreateOnlyOneActiveSession()
    {
        var starts = await Task.WhenAll(
            _study.StartAsync(Intensity.Normal),
            _study.StartAsync(Intensity.Heavy));

        Assert.Equal(starts[0], starts[1]);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_dir, "study.db"),
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sessions WHERE ended_at IS NULL";
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
        await _study.FinishAsync();
    }

    [Fact]
    public async Task Answer_PersistsLog_AndAdvancesCard()
    {
        await _study.StartAsync(Intensity.Normal);
        var item = _study.PeekNext()!;
        long cardId = item.CardId;

        await _study.AnswerAsync(item, Grade.Good);

        // card advanced beyond "new"
        var card = await _db.GetCardAsync(cardId);
        Assert.NotNull(card);
        Assert.Equal(1, card!.Reps);
        Assert.NotNull(card.LastReviewUtc);

        var insights = await _db.GetInsightsAsync(DateTime.UtcNow);
        Assert.Equal(1, insights.ReviewsToday);
        Assert.Equal(1, insights.TotalReviews);
        await _study.FinishAsync();
    }

    [Fact]
    public async Task ShutdownBarrierWaitsForInFlightAnswerBeforeDatabaseCanBeDisposed()
    {
        await _study.StartAsync(Intensity.Normal);
        var item = _study.PeekNext()!;
        await using var blocker = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_dir, "study.db"),
            Pooling = false,
        }.ToString());
        await blocker.OpenAsync();
        await using (var begin = blocker.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE";
            await begin.ExecuteNonQueryAsync();
        }

        var answer = _study.AnswerAsync(item, Grade.Good);
        await Task.Delay(50);
        var answerWaitedForWriter = !answer.IsCompleted;

        var shutdownBarrier = _study.WaitForPendingOperationsAsync();
        await Task.Delay(50);
        var barrierWaitedForAnswer = !shutdownBarrier.IsCompleted;

        await using (var rollback = blocker.CreateCommand())
        {
            rollback.CommandText = "ROLLBACK";
            await rollback.ExecuteNonQueryAsync();
        }
        await answer.WaitAsync(TimeSpan.FromSeconds(5));
        await shutdownBarrier.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(answerWaitedForWriter);
        Assert.True(barrierWaitedForAnswer);
        Assert.Equal(1, await CountReviewsForCardAsync(item.CardId));
        Assert.True(_study.IsActive);
    }

    [Fact]
    public async Task Answer_WithHint_PersistsReproducibleShadowPredictionWithoutChangingDueDate()
    {
        await _study.StartAsync(Intensity.Normal);
        var item = _study.PeekNext()!;

        await _study.AnswerAsync(item, Grade.Good, new StudyAnswerContext(HintUsed: true));

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_dir, "study.db"),
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT l.hint_used, p.raw_recall, p.predicted_recall, p.model_version,
                   p.features_json, mp.version, l.next_due, c.due
            FROM review_logs l
            JOIN model_predictions p ON p.review_log_id=l.id
            JOIN model_profiles mp ON mp.id=p.profile_id
            JOIN cards c ON c.id=l.card_id
            WHERE l.card_id=$card
            """;
        command.Parameters.AddWithValue("$card", item.CardId);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(1, reader.GetInt32(3));
        Assert.Equal(reader.GetInt32(3), reader.GetInt32(5));
        Assert.Equal(reader.GetDouble(1), reader.GetDouble(2)); // baseline profile falls back to raw FSRS
        Assert.Contains("\"HintUsed\":1", reader.GetString(4));
        Assert.Equal(reader.GetString(6), reader.GetString(7)); // only FSRS selected the due date
        await _study.FinishAsync();
    }

    [Fact]
    public async Task DuplicateConcurrentAnswer_PersistsOnlyOneReview()
    {
        await _study.StartAsync(Intensity.Normal);
        var item = _study.PeekNext()!;

        await Task.WhenAll(
            _study.AnswerAsync(item, Grade.Good),
            _study.AnswerAsync(item, Grade.Again));

        var card = await _db.GetCardAsync(item.CardId);
        Assert.Equal(1, card!.Reps);
        Assert.Equal(1, await CountReviewsForCardAsync(item.CardId));
        await _study.FinishAsync();
    }

    [Fact]
    public async Task RepeatedOldAnswerCannotGradeTheRequeuedAgainCard()
    {
        await _study.StartAsync(Intensity.Normal);
        var item = _study.PeekNext()!;

        Assert.NotNull(await _study.AnswerAsync(item, Grade.Again));
        Assert.Null(await _study.AnswerAsync(item, Grade.Good));

        var card = await _db.GetCardAsync(item.CardId);
        Assert.Equal(1, card!.Reps);
        Assert.Equal(1, await CountReviewsForCardAsync(item.CardId));
        await _study.FinishAsync();
    }

    [Fact]
    public async Task ReenteringSameCurrentCardDoesNotRestartItsTiming()
    {
        await _study.StartAsync(Intensity.Normal);
        var item = _study.PeekNext()!;
        await Task.Delay(450);

        Assert.Equal(item.CardId, _study.PeekNext()!.CardId);
        await Task.Delay(450);
        await _study.AnswerAsync(item, Grade.Good);

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_dir, "study.db"),
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT raw_elapsed_ms FROM review_logs WHERE card_id=$card";
        command.Parameters.AddWithValue("$card", item.CardId);
        var rawElapsedMs = (double)(await command.ExecuteScalarAsync())!;

        Assert.True(rawElapsedMs >= 750, $"Expected time from both presentations, got {rawElapsedMs:0} ms.");
    }

    private async Task<long> CountReviewsForCardAsync(long cardId)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_dir, "study.db"),
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM review_logs WHERE card_id=$card";
        command.Parameters.AddWithValue("$card", cardId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Answer_Again_RequeuesShortLearningStep_InSession()
    {
        await _study.StartAsync(Intensity.Normal);
        var item = _study.PeekNext()!;
        var requeuedAt = await _study.AnswerAsync(item, Grade.Again);

        Assert.NotNull(requeuedAt); // 1-minute step comes back inside the 30-minute window
        Assert.True(requeuedAt!.Value > DateTime.UtcNow.AddSeconds(-5));
        Assert.True(_study.Remaining > 0, "the requeued card must remain in the session queue");
    }

    [Fact]
    public async Task Finish_WritesSessionCounters()
    {
        await _study.StartAsync(Intensity.Normal);
        var first = _study.PeekNext()!;
        await _study.AnswerAsync(first, Grade.Good);
        var second = _study.PeekNext();
        if (second is not null)
            await _study.AnswerAsync(second, Grade.Again);

        var summary = await _study.FinishAsync();

        Assert.Equal(2, summary.Reviews);
        Assert.Equal(1, summary.Good);
        Assert.Equal(1, summary.Again);
        Assert.True(summary.Duration >= TimeSpan.Zero);
        Assert.False(_study.IsActive);
    }

    [Fact]
    public async Task Finish_WaitsForAnswerToPersistAndUpdateSessionCounters()
    {
        var engine = new BlockingScheduleEngine();
        var study = new StudyService(_db, engine);
        await study.StartAsync(Intensity.Normal);
        var item = study.PeekNext()!;

        var answer = Task.Run(() => study.AnswerAsync(item, Grade.Good));
        Assert.True(engine.ScheduleEntered.Wait(TimeSpan.FromSeconds(5)), "answer should enter the scheduler");
        var finish = study.FinishAsync();
        bool finishCompletedWhileAnswerWasBlocked;
        try
        {
            await Task.Delay(100);
            finishCompletedWhileAnswerWasBlocked = finish.IsCompleted;
        }
        finally
        {
            engine.ReleaseSchedule.Set();
        }

        var answerError = await Record.ExceptionAsync(async () => { await answer; });
        var summary = await finish;

        Assert.False(finishCompletedWhileAnswerWasBlocked, "finishing must wait until answer persistence and counters complete");
        Assert.Null(answerError);
        Assert.Equal(1, summary.Reviews);
        Assert.Equal(1, summary.Good);
        Assert.Equal(1, await CountReviewsForCardAsync(item.CardId));
        var card = await _db.GetCardAsync(item.CardId);
        Assert.Equal(1, card!.Reps);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_dir, "study.db"),
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT reviews, good FROM sessions WHERE ended_at IS NOT NULL ORDER BY id DESC LIMIT 1";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(1, reader.GetInt32(1));
    }

    [Fact]
    public async Task PauseWaitsForInFlightAnswerToCommitBeforeFreezingTheSession()
    {
        var engine = new BlockingScheduleEngine();
        var study = new StudyService(_db, engine);
        await study.StartAsync(Intensity.Normal);
        var item = study.PeekNext()!;

        var answer = Task.Run(() => study.AnswerAsync(item, Grade.Good));
        Assert.True(engine.ScheduleEntered.Wait(TimeSpan.FromSeconds(5)), "answer should enter the scheduler");
        var pause = study.PauseAsync();
        try
        {
            await Task.Delay(100);
            Assert.False(pause.IsCompleted, "pause must wait for the pending rating to finish saving");
        }
        finally
        {
            engine.ReleaseSchedule.Set();
        }

        await answer;
        await pause;

        Assert.True(study.IsPaused);
        Assert.Equal(1, study.CompletedReviews);
        Assert.Equal(1, await CountReviewsForCardAsync(item.CardId));
        Assert.Null(study.PeekNext());
        await study.ResumeAsync();
        Assert.NotNull(study.PeekNext());
        await study.FinishAsync();
    }

    [Fact]
    public async Task Pause_ExcludesMonotonicTimeWhenWallClockChanges()
    {
        var clock = new AdjustableTimeProvider();
        var engine = new ElapsedCapturingEngine();
        var study = new StudyService(_db, engine, clock);
        await study.StartAsync(Intensity.Normal);
        var item = study.PeekNext()!;

        clock.AdvanceMonotonic(TimeSpan.FromMilliseconds(600));
        await study.PauseAsync();
        clock.AdvanceMonotonic(TimeSpan.FromSeconds(1));
        clock.AdjustWallClock(TimeSpan.FromHours(-1));
        await study.ResumeAsync();
        clock.AdvanceMonotonic(TimeSpan.FromMilliseconds(100));

        await study.AnswerAsync(item, Grade.Good);

        Assert.Equal(700, engine.ElapsedMs);
        await study.FinishAsync();
    }

    private sealed class BlockingScheduleEngine : Scheduling.ISrsEngine
    {
        private readonly Scheduling.FsrsCoreEngine _inner = new(enableFuzzing: false);

        public ManualResetEventSlim ScheduleEntered { get; } = new();
        public ManualResetEventSlim ReleaseSchedule { get; } = new();
        public string Version => _inner.Version;
        public double DesiredRetention => _inner.DesiredRetention;
        public double PredictRecall(Scheduling.SrsCardSnapshot card, DateTime nowUtc) => _inner.PredictRecall(card, nowUtc);

        public Scheduling.SchedulingOutcome Schedule(
            Scheduling.SrsCardSnapshot card,
            Grade grade,
            DateTime nowUtc,
            int? elapsedMs = null)
        {
            ScheduleEntered.Set();
            if (!ReleaseSchedule.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Test did not release the blocked scheduler.");
            return _inner.Schedule(card, grade, nowUtc, elapsedMs);
        }
    }

    [Fact]
    public void Timing_Invalid_WhenWindowUnfocusedTooLong()
    {
        var clock = new AdjustableTimeProvider();
        var t = new Scheduling.EffectiveTiming(clock);
        t.AddUnfocused(30_000);
        clock.AdvanceMonotonic(TimeSpan.FromMilliseconds(50));
        var result = t.Finish();

        Assert.False(result.Valid);
        Assert.True(result.UnfocusedMs >= 30_000);
        Assert.Equal(50, result.RawMs);
    }

    [Fact]
    public async Task Timing_Valid_ForFocusedNormalAnswer()
    {
        var t = new Scheduling.EffectiveTiming();
        t.AddUnfocused(100);
        await Task.Delay(400);
        var result = t.Finish();

        Assert.True(result.Valid);
        Assert.True(result.EffectiveMs >= 300);
    }

    [Fact]
    public void Timing_IgnoresWallClockChanges()
    {
        var clock = new AdjustableTimeProvider();
        var timing = new Scheduling.EffectiveTiming(clock);

        clock.AdvanceMonotonic(TimeSpan.FromMilliseconds(600));
        clock.AdjustWallClock(TimeSpan.FromHours(-1));

        var result = timing.Finish();

        Assert.Equal(600, result.RawMs);
        Assert.Equal(600, result.EffectiveMs);
        Assert.True(result.Valid);
    }

    private sealed class AdjustableTimeProvider : TimeProvider
    {
        private long _timestamp;
        private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void AdvanceMonotonic(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
        public void AdjustWallClock(TimeSpan change) => _utcNow += change;
    }

    private sealed class ElapsedCapturingEngine : Scheduling.ISrsEngine
    {
        private readonly Scheduling.FsrsCoreEngine _inner = new(enableFuzzing: false);

        public int? ElapsedMs { get; private set; }
        public string Version => _inner.Version;
        public double DesiredRetention => _inner.DesiredRetention;
        public double PredictRecall(Scheduling.SrsCardSnapshot card, DateTime nowUtc) => _inner.PredictRecall(card, nowUtc);

        public Scheduling.SchedulingOutcome Schedule(
            Scheduling.SrsCardSnapshot card,
            Grade grade,
            DateTime nowUtc,
            int? elapsedMs = null)
        {
            ElapsedMs = elapsedMs;
            return _inner.Schedule(card, grade, nowUtc, elapsedMs);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _study.FinishAsync();
        await _db.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
