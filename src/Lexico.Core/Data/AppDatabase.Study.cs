using System.Globalization;
using Lexico.Core.Domain;
using Microsoft.Data.Sqlite;

namespace Lexico.Core.Data;

public sealed record DueSnapshot(int DueLearning, int DueReview, int NewAvailable, int DueReviewWithin4Days);

public sealed record StateCounts(int New, int Learning, int Review);

public sealed record DayLoad(DateTime LocalDate, int DueCount);

public sealed record InsightsData(
    StateCounts Counts,
    IReadOnlyList<DayLoad> Next7Days,
    int ReviewsToday,
    double Retention30d,
    long TotalReviews,
    double AvgSecNew,
    double AvgSecReview);

public sealed partial class AppDatabase
{
    // ---- cards ----

    /// <summary>Create study cards for every deck word that does not have one yet.</summary>
    public Task<int> EnsureCardsForDeckAsync(long deckId) => RunAsync(() =>
    {
        var now = Fmt(DateTime.UtcNow);
        return Exec("""
            INSERT INTO cards(word_id, deck_id, state, step, due, created_at)
            SELECT dw.word_id, dw.deck_id, 0, 0, @now, @now
            FROM deck_words dw
            WHERE dw.deck_id = @deck
              AND NOT EXISTS (SELECT 1 FROM cards c WHERE c.word_id = dw.word_id AND c.deck_id = dw.deck_id)
            """, ("@deck", deckId), ("@now", now));
    });

    public Task<CardRecord?> GetCardAsync(long cardId) => RunAsync(() =>
    {
        using var cmd = Cmd("""
            SELECT id, word_id, deck_id, state, step, stability, difficulty, due, last_review, reps, lapses, created_at
            FROM cards WHERE id = @id
            """, ("@id", cardId));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadCard(reader) : null;
    });

    private static CardRecord ReadCard(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetInt64(2),
        (CardState)r.GetInt32(3),
        r.IsDBNull(4) ? null : r.GetInt32(4),
        r.IsDBNull(5) ? null : r.GetDouble(5),
        r.IsDBNull(6) ? null : r.GetDouble(6),
        ParseUtc(r.GetString(7)),
        r.IsDBNull(8) ? null : ParseUtc(r.GetString(8)),
        r.GetInt32(9), r.GetInt32(10),
        ParseUtc(r.GetString(11)));

    public Task<DueSnapshot> GetDueSnapshotAsync(DateTime nowUtc) => RunAsync(() =>
    {
        var now = Fmt(nowUtc);
        var horizon = Fmt(nowUtc.AddDays(4));
        int dueLearning = Scalar<long>(
            "SELECT COUNT(*) FROM cards WHERE state IN (0, 2) AND reps > 0 AND due <= @n", ("@n", now)) is long a ? (int)a : 0;
        int dueReview = Scalar<long>(
            "SELECT COUNT(*) FROM cards WHERE state = 1 AND due <= @n", ("@n", now)) is long b ? (int)b : 0;
        int fresh = Scalar<long>(
            "SELECT COUNT(*) FROM cards WHERE state = 0 AND reps = 0", ("@n", now)) is long c ? (int)c : 0;
        int within4 = Scalar<long>(
            "SELECT COUNT(*) FROM cards WHERE state = 1 AND due <= @h", ("@h", horizon)) is long d ? (int)d : 0;
        return new DueSnapshot(dueLearning, dueReview, fresh, within4);
    });

    /// <summary>Due learning/relearning cards (short-interval steps), oldest first.</summary>
    public Task<List<CardRecord>> GetDueLearningCardsAsync(DateTime nowUtc, int limit) => RunAsync(() => QueryCards(
        "SELECT id, word_id, deck_id, state, step, stability, difficulty, due, last_review, reps, lapses, created_at " +
        "FROM cards WHERE state IN (0, 2) AND reps > 0 AND due <= @n ORDER BY due LIMIT @l",
        ("@n", Fmt(nowUtc)), ("@l", limit)));

    /// <summary>Due review cards, most overdue first (a good proxy for lowest retrievability).</summary>
    public Task<List<CardRecord>> GetDueReviewCardsAsync(DateTime maxDueUtc, int limit, DateTime? minDueUtc = null) => RunAsync(() => QueryCards(
        "SELECT id, word_id, deck_id, state, step, stability, difficulty, due, last_review, reps, lapses, created_at " +
        "FROM cards WHERE state = 1 AND due <= @max AND (@min IS NULL OR due >= @min) ORDER BY due LIMIT @l",
        ("@max", Fmt(maxDueUtc)), ("@min", minDueUtc is null ? null : Fmt(minDueUtc.Value)), ("@l", limit)));

    /// <summary>Never-seen cards in deck order.</summary>
    public Task<List<CardRecord>> GetNewCardsAsync(int limit, long? deckId = null) => RunAsync(() => QueryCards(
        "SELECT c.id, c.word_id, c.deck_id, c.state, c.step, c.stability, c.difficulty, c.due, c.last_review, c.reps, c.lapses, c.created_at " +
        "FROM cards c JOIN deck_words dw ON dw.deck_id = c.deck_id AND dw.word_id = c.word_id " +
        "WHERE c.state = 0 AND c.reps = 0 AND (@deck IS NULL OR c.deck_id = @deck) " +
        "ORDER BY c.deck_id, dw.position LIMIT @l",
        ("@deck", deckId), ("@l", limit)));

    private List<CardRecord> QueryCards(string sql, params (string, object?)[] args)
    {
        var cards = new List<CardRecord>();
        using var cmd = Cmd(sql, args);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            cards.Add(ReadCard(reader));
        return cards;
    }

    /// <summary>Atomically persist a reviewed card plus its immutable log row.</summary>
    public Task AnswerCardAsync(CardRecord updated, ReviewLogRow log) => RunAsync(() =>
    {
        using var tx = _conn.BeginTransaction();
        using (var cmd = Cmd("""
            UPDATE cards SET state=@st, step=@sp, stability=@sb, difficulty=@df, due=@du, last_review=@lr, reps=@rp, lapses=@lp
            WHERE id=@id
            """,
            ("@st", (int)updated.State), ("@sp", updated.Step),
            ("@sb", updated.Stability), ("@df", updated.Difficulty),
            ("@du", Fmt(updated.DueUtc)), ("@lr", updated.LastReviewUtc is null ? null : Fmt(updated.LastReviewUtc.Value)),
            ("@rp", updated.Reps), ("@lp", updated.Lapses), ("@id", updated.Id)))
        {
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
        using (var cmd = Cmd("""
            INSERT INTO review_logs(card_id, reviewed_at, rating, state_before, step_before, stability_before, difficulty_before,
                                    scheduled_days_before, reps_before, raw_elapsed_ms, elapsed_ms, timing_valid,
                                    fsrs_retrievability, engine_version, next_due, next_interval_days)
            VALUES(@card, @at, @rating, @sb, @spb, @stb, @dfb, @sched, @repsb, @raw, @el, @valid, @r, @ver, @nd, @nid)
            """,
            ("@card", log.CardId), ("@at", Fmt(log.ReviewedAtUtc)), ("@rating", (int)log.Rating),
            ("@sb", (int)log.StateBefore), ("@spb", log.StepBefore), ("@stb", log.StabilityBefore), ("@dfb", log.DifficultyBefore),
            ("@sched", log.ScheduledDaysBefore), ("@repsb", log.RepsBefore),
            ("@raw", log.RawElapsedMs), ("@el", log.EffectiveElapsedMs), ("@valid", log.TimingValid ? 1 : 0),
            ("@r", log.FsrsRetrievability), ("@ver", log.EngineVersion),
            ("@nd", Fmt(log.NextDueUtc)), ("@nid", log.NextIntervalDays)))
        {
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return 0;
    });

    // ---- sessions & plans ----

    public Task<long> StartSessionAsync(Intensity intensity, DateTime startedUtc) => RunAsync(() =>
    {
        Exec("INSERT INTO sessions(started_at, intensity) VALUES(@s, @i)", ("@s", Fmt(startedUtc)), ("@i", (int)intensity));
        return Scalar<long>("SELECT MAX(id) FROM sessions");
    });

    public Task FinishSessionAsync(long sessionId, DateTime endedUtc, int reviews, int again, int hard, int good, int easy) => RunAsync(() =>
    {
        Exec("""
            UPDATE sessions SET ended_at=@e, reviews=@r, again=@a, hard=@h, good=@g, easy=@y WHERE id=@id
            """,
            ("@e", Fmt(endedUtc)), ("@r", reviews), ("@a", again), ("@h", hard), ("@g", good), ("@y", easy), ("@id", sessionId));
        return 0;
    });

    public Task SaveDailyPlanAsync(string localDate, Intensity intensity, int reviewCount, int newCount, double estimatedMinutes) => RunAsync(() =>
    {
        Exec("""
            INSERT INTO daily_plans(date, intensity, review_count, new_count, estimated_minutes, created_at)
            VALUES(@d, @i, @r, @n, @m, @c)
            ON CONFLICT(date, intensity) DO UPDATE SET review_count=@r, new_count=@n, estimated_minutes=@m, created_at=@c
            """,
            ("@d", localDate), ("@i", (int)intensity), ("@r", reviewCount), ("@n", newCount), ("@m", estimatedMinutes), ("@c", Fmt(DateTime.UtcNow)));
        return 0;
    });

    // ---- insights & baselines ----

    public Task<double> GetAvgSecondsAsync(bool newCards, int sampleLimit = 1000) => RunAsync(() =>
    {
        var avg = Scalar<object?>(
            "SELECT AVG(elapsed_ms) FROM (SELECT elapsed_ms FROM review_logs WHERE timing_valid = 1 AND reps_before " +
            (newCards ? "= 0" : "> 0") + " ORDER BY id DESC LIMIT " + sampleLimit + ")");
        return avg is null or DBNull ? -1.0 : Convert.ToDouble(avg, CultureInfo.InvariantCulture) / 1000.0;
    });

    public Task<InsightsData> GetInsightsAsync(DateTime nowUtc) => RunAsync(() =>
    {
        int fresh = (int)Scalar<long>("SELECT COUNT(*) FROM cards WHERE state = 0 AND reps = 0");
        int learning = (int)Scalar<long>("SELECT COUNT(*) FROM cards WHERE state IN (0,2) AND reps > 0");
        int review = (int)Scalar<long>("SELECT COUNT(*) FROM cards WHERE state = 1");

        var horizon = Fmt(nowUtc.AddDays(7));
        var now = Fmt(nowUtc);
        var byDay = new Dictionary<DateTime, int>();
        using (var cmd = Cmd("SELECT due FROM cards WHERE state = 1 AND due > @n AND due <= @h", ("@n", now), ("@h", horizon)))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var local = ParseUtc(reader.GetString(0)).ToLocalTime().Date;
                byDay[local] = byDay.TryGetValue(local, out var c) ? c + 1 : 1;
            }
        }
        var loads = Enumerable.Range(0, 7)
            .Select(i => new DayLoad(nowUtc.ToLocalTime().Date.AddDays(i), byDay.GetValueOrDefault(nowUtc.ToLocalTime().Date.AddDays(i))))
            .ToList();

        var todayLocal = nowUtc.ToLocalTime().Date;
        var todayStartUtc = DateTime.SpecifyKind(todayLocal, DateTimeKind.Local).ToUniversalTime();
        int reviewsToday = (int)Scalar<long>("SELECT COUNT(*) FROM review_logs WHERE reviewed_at >= @s", ("@s", Fmt(todayStartUtc)));
        var retentionRaw = Scalar<object?>(
            "SELECT AVG(CASE WHEN rating >= 3 THEN 1.0 ELSE 0.0 END) FROM review_logs WHERE reviewed_at >= @s",
            ("@s", Fmt(nowUtc.AddDays(-30))));
        double retention30 = retentionRaw is null or DBNull ? -1 : Convert.ToDouble(retentionRaw, CultureInfo.InvariantCulture);
        long total = Scalar<long>("SELECT COUNT(*) FROM review_logs");

        double avgNew = GetAvgSecondsSync(isNew: true);
        double avgReview = GetAvgSecondsSync(isNew: false);
        return new InsightsData(new StateCounts(fresh, learning, review), loads, reviewsToday, retention30, total, avgNew, avgReview);

        double GetAvgSecondsSync(bool isNew)
        {
            var avg = Scalar<object?>(
                "SELECT AVG(elapsed_ms) FROM (SELECT elapsed_ms FROM review_logs WHERE timing_valid = 1 AND reps_before " +
                (isNew ? "= 0" : "> 0") + " ORDER BY id DESC LIMIT 1000)");
            return avg is null or DBNull ? -1.0 : Convert.ToDouble(avg, CultureInfo.InvariantCulture) / 1000.0;
        }
    });
}
