using System.Globalization;
using System.Text.Json;
using Lexico.Core.Domain;
using Lexico.Core.Study;
using Microsoft.Data.Sqlite;

namespace Lexico.Core.Data;

public sealed record DueSnapshot(int DueLearning, int DueReview, int NewAvailable, int DueReviewWithin4Days);

public sealed record PlanningCardRecord(CardRecord Card, int? FrequencyRank);

public sealed record TimingSamples(int Count, double TotalSeconds);

public sealed record PlannerTimingStats(
    TimingSamples NewCards,
    TimingSamples LearningSteps,
    TimingSamples DueReview,
    TimingSamples OverdueReview,
    TimingSamples EarlyReview);

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

    private static CardRecord ReadCard(SqliteDataReader r) => ReadCard(r, 0);

    private static CardRecord ReadCard(SqliteDataReader r, int offset) => new(
        r.GetInt64(offset), r.GetInt64(offset + 1), r.GetInt64(offset + 2),
        (CardState)r.GetInt32(offset + 3),
        r.IsDBNull(offset + 4) ? null : r.GetInt32(offset + 4),
        r.IsDBNull(offset + 5) ? null : r.GetDouble(offset + 5),
        r.IsDBNull(offset + 6) ? null : r.GetDouble(offset + 6),
        ParseUtc(r.GetString(offset + 7)),
        r.IsDBNull(offset + 8) ? null : ParseUtc(r.GetString(offset + 8)),
        r.GetInt32(offset + 9), r.GetInt32(offset + 10),
        ParseUtc(r.GetString(offset + 11)));

    public Task<DueSnapshot> GetDueSnapshotAsync(DateTime nowUtc, long? deckId = null) => RunAsync(() =>
    {
        var now = Fmt(nowUtc);
        var horizon = Fmt(nowUtc.AddDays(4));
        int dueLearning = Scalar<long>(
            "SELECT COUNT(*) FROM cards c WHERE (@deck IS NULL OR c.deck_id = @deck) AND c.state IN (0, 2) AND c.reps > 0 AND c.due <= @n " +
            "AND NOT EXISTS (SELECT 1 FROM card_marks m WHERE m.card_id=c.id AND m.mastered=1)",
            ("@deck", deckId), ("@n", now)) is long a ? (int)a : 0;
        int dueReview = Scalar<long>(
            "SELECT COUNT(*) FROM cards c WHERE (@deck IS NULL OR c.deck_id = @deck) AND c.state = 1 AND c.due <= @n " +
            "AND NOT EXISTS (SELECT 1 FROM card_marks m WHERE m.card_id=c.id AND m.mastered=1)",
            ("@deck", deckId), ("@n", now)) is long b ? (int)b : 0;
        int fresh = Scalar<long>(
            "SELECT COUNT(*) FROM cards c WHERE (@deck IS NULL OR c.deck_id = @deck) AND c.state = 0 AND c.reps = 0 " +
            "AND NOT EXISTS (SELECT 1 FROM card_marks m WHERE m.card_id=c.id AND m.mastered=1)",
            ("@deck", deckId)) is long c ? (int)c : 0;
        int within4 = Scalar<long>(
            "SELECT COUNT(*) FROM cards c WHERE (@deck IS NULL OR c.deck_id = @deck) AND c.state = 1 AND c.due <= @h " +
            "AND NOT EXISTS (SELECT 1 FROM card_marks m WHERE m.card_id=c.id AND m.mastered=1)",
            ("@deck", deckId), ("@h", horizon)) is long d ? (int)d : 0;
        return new DueSnapshot(dueLearning, dueReview, fresh, within4);
    });

    /// <summary>Cards eligible for today's plan, including reviews due within the early-review horizon.</summary>
    public Task<List<PlanningCardRecord>> GetPlanningCardsAsync(DateTime nowUtc, DateTime earlyReviewHorizonUtc, long? deckId = null) => RunAsync(() =>
    {
        var now = Fmt(nowUtc);
        var horizon = Fmt(earlyReviewHorizonUtc);
        var cards = new List<PlanningCardRecord>();
        using var cmd = Cmd("""
            SELECT c.id, c.word_id, c.deck_id, c.state, c.step, c.stability, c.difficulty, c.due,
                   c.last_review, c.reps, c.lapses, c.created_at, w.frequency_rank
            FROM cards c
            LEFT JOIN words w ON w.id = c.word_id
            WHERE (@deck IS NULL OR c.deck_id = @deck)
              AND NOT EXISTS (SELECT 1 FROM card_marks m WHERE m.card_id=c.id AND m.mastered=1)
              AND ((c.state IN (0, 2) AND c.reps > 0 AND c.due <= @now)
                OR (c.state = 1 AND c.due <= @horizon)
                OR (c.state = 0 AND c.reps = 0
                    AND EXISTS (SELECT 1 FROM deck_words dw WHERE dw.deck_id=c.deck_id AND dw.word_id=c.word_id)))
            ORDER BY c.due, c.id
            """, ("@now", now), ("@horizon", horizon), ("@deck", deckId));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            cards.Add(new PlanningCardRecord(
                ReadCard(reader),
                reader.IsDBNull(12) ? null : reader.GetInt32(12)));
        }
        return cards;
    });

    /// <summary>
    /// Effective response-time samples from recent valid logs. A previous log's next_due
    /// gives the due time before the current review without changing the immutable history schema.
    /// </summary>
    public Task<PlannerTimingStats> GetPlannerTimingStatsAsync(int sampleLimit = 1000) => RunAsync(() =>
    {
        var counts = new int[5];
        var totals = new double[5];
        using var cmd = Cmd("""
            SELECT l.reps_before, l.state_before, l.elapsed_ms, l.reviewed_at,
                   (SELECT p.next_due FROM review_logs p
                    WHERE p.card_id = l.card_id AND p.id < l.id
                    ORDER BY p.id DESC LIMIT 1)
            FROM review_logs l
            WHERE l.timing_valid = 1 AND l.elapsed_ms > 0
            ORDER BY l.id DESC
            LIMIT @limit
            """, ("@limit", Math.Clamp(sampleLimit, 1, 100_000)));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            double seconds = reader.GetDouble(2) / 1000.0;
            if (!double.IsFinite(seconds) || seconds <= 0)
                continue;

            int bucket;
            int repsBefore = reader.GetInt32(0);
            var state = (CardState)reader.GetInt32(1);
            if (repsBefore == 0)
            {
                bucket = 0;
            }
            else if (state is CardState.Learning or CardState.Relearning)
            {
                bucket = 1;
            }
            else if (state == CardState.Review)
            {
                bucket = 2;
                if (!reader.IsDBNull(4))
                {
                    var dueBefore = ParseUtc(reader.GetString(4));
                    var reviewedAt = ParseUtc(reader.GetString(3));
                    var lateness = reviewedAt - dueBefore;
                    if (lateness < TimeSpan.Zero)
                        bucket = 4;
                    else if (lateness >= TimeSpan.FromDays(1))
                        bucket = 3;
                }
            }
            else
            {
                continue;
            }

            counts[bucket]++;
            totals[bucket] += seconds;
        }

        TimingSamples Samples(int index) => new(counts[index], totals[index]);
        return new PlannerTimingStats(Samples(0), Samples(1), Samples(2), Samples(3), Samples(4));
    });

    /// <summary>Due learning/relearning cards (short-interval steps), oldest first.</summary>
    public Task<List<CardRecord>> GetDueLearningCardsAsync(DateTime nowUtc, int limit) => RunAsync(() => QueryCards(
        "SELECT id, word_id, deck_id, state, step, stability, difficulty, due, last_review, reps, lapses, created_at " +
        "FROM cards c WHERE state IN (0, 2) AND reps > 0 AND due <= @n " +
        "AND NOT EXISTS (SELECT 1 FROM card_marks m WHERE m.card_id=c.id AND m.mastered=1) ORDER BY due LIMIT @l",
        ("@n", Fmt(nowUtc)), ("@l", limit)));

    /// <summary>Due review cards, most overdue first (a good proxy for lowest retrievability).</summary>
    public Task<List<CardRecord>> GetDueReviewCardsAsync(DateTime maxDueUtc, int limit, DateTime? minDueUtc = null) => RunAsync(() => QueryCards(
        "SELECT id, word_id, deck_id, state, step, stability, difficulty, due, last_review, reps, lapses, created_at " +
        "FROM cards c WHERE state = 1 AND due <= @max AND (@min IS NULL OR due >= @min) " +
        "AND NOT EXISTS (SELECT 1 FROM card_marks m WHERE m.card_id=c.id AND m.mastered=1) ORDER BY due LIMIT @l",
        ("@max", Fmt(maxDueUtc)), ("@min", minDueUtc is null ? null : Fmt(minDueUtc.Value)), ("@l", limit)));

    /// <summary>Never-seen cards in deck order.</summary>
    public Task<List<CardRecord>> GetNewCardsAsync(int limit, long? deckId = null) => RunAsync(() => QueryCards(
        "SELECT c.id, c.word_id, c.deck_id, c.state, c.step, c.stability, c.difficulty, c.due, c.last_review, c.reps, c.lapses, c.created_at " +
        "FROM cards c JOIN deck_words dw ON dw.deck_id = c.deck_id AND dw.word_id = c.word_id " +
        "WHERE c.state = 0 AND c.reps = 0 AND (@deck IS NULL OR c.deck_id = @deck) " +
        "AND NOT EXISTS (SELECT 1 FROM card_marks m WHERE m.card_id=c.id AND m.mastered=1) " +
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

    public Task<RecallModelProfileRecord?> GetActiveRecallModelProfileAsync() => RunAsync(() =>
    {
        using var cmd = Cmd("""
            SELECT id, version, weights, trained_at, metrics, is_active, accepted, trained_through_log_id
            FROM model_profiles
            WHERE kind='personal-recall-calibrator' AND is_active=1
            ORDER BY version DESC LIMIT 1
            """);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;
        return new RecallModelProfileRecord(
            reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), ParseUtc(reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt32(5) != 0, reader.GetInt32(6) != 0,
            reader.IsDBNull(7) ? null : reader.GetInt64(7));
    });

    public Task<IReadOnlyList<CardReviewHistorySample>> GetRecentCardReviewHistoryAsync(long cardId, int limit = 100) => RunAsync(() =>
    {
        var history = new List<CardReviewHistorySample>();
        using var cmd = Cmd("""
            SELECT rating, elapsed_ms, timing_valid FROM review_logs
            WHERE card_id=@card ORDER BY id DESC LIMIT @limit
            """, ("@card", cardId), ("@limit", Math.Clamp(limit, 1, 1000)));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            history.Add(new CardReviewHistorySample((Grade)reader.GetInt32(0), reader.GetDouble(1), reader.GetInt32(2) != 0));
        return (IReadOnlyList<CardReviewHistorySample>)history;
    });

    public Task<RecallTrainingSnapshot> GetRecallTrainingSnapshotAsync(int maxExamples = 5000) => RunAsync(() =>
    {
        long latestLogId = Scalar<long>("SELECT COALESCE(MAX(id), 0) FROM review_logs");
        long trainedThrough = Scalar<long>("SELECT COALESCE(MAX(trained_through_log_id), 0) FROM model_profiles WHERE kind='personal-recall-calibrator'");
        int latestVersion = (int)Scalar<long>("SELECT COALESCE(MAX(version), 0) FROM model_profiles WHERE kind='personal-recall-calibrator'");
        var observations = new List<RecallTrainingObservation>();
        if (latestLogId - trainedThrough >= 50)
        {
            using var cmd = Cmd("""
                SELECT l.id, l.reviewed_at, l.rating,
                       COALESCE(p.raw_recall, p.predicted_recall, l.fsrs_retrievability), p.features_json,
                       l.state_before, l.scheduled_days_before, l.elapsed_ms, l.timing_valid, l.hint_used,
                       COALESCE((
                           SELECT AVG(CASE WHEN recent.rating=1 THEN 0.0 ELSE 1.0 END)
                           FROM (SELECT prior.rating FROM review_logs prior
                                 WHERE prior.card_id=l.card_id AND prior.id<l.id
                                 ORDER BY prior.id DESC LIMIT 10) recent
                       ), 0.5),
                       (SELECT COUNT(*) FROM review_logs prior
                        WHERE prior.card_id=l.card_id AND prior.id<l.id
                          AND prior.rating=1 AND prior.state_before=1)
                FROM review_logs l
                LEFT JOIN model_predictions p ON p.review_log_id=l.id
                WHERE (p.features_json IS NOT NULL AND p.features_json <> '{}')
                   OR (l.fsrs_retrievability IS NOT NULL AND COALESCE(p.features_json, '{}')='{}')
                ORDER BY l.id DESC LIMIT @limit
                """, ("@limit", Math.Clamp(maxExamples, 200, 50_000)));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var featuresJson = reader.IsDBNull(4) ? "{}" : reader.GetString(4);
                if (featuresJson == "{}")
                {
                    var state = (CardState)reader.GetInt32(5);
                    double interval = reader.IsDBNull(6) ? 0 : Math.Clamp(reader.GetDouble(6), 0, 36_500);
                    double priorLapses = Math.Clamp(reader.GetDouble(11), 0, 1000);
                    var features = new PersonalRecallCalibrator.FeatureVector(
                        reader.IsDBNull(3) ? 0.5 : reader.GetDouble(3),
                        RelativeThinkingTime: 0,
                        reader.GetDouble(10),
                        Math.Log(1 + priorLapses) / Math.Log(1001),
                        reader.GetInt32(9) != 0 ? 1 : 0,
                        Math.Log(1 + interval) / Math.Log(36_501),
                        state == CardState.Learning ? 1 : 0,
                        state == CardState.Review ? 1 : 0,
                        state == CardState.Relearning ? 1 : 0);
                    featuresJson = JsonSerializer.Serialize(features);
                }

                observations.Add(new RecallTrainingObservation(
                    reader.GetInt64(0), ParseUtc(reader.GetString(1)), (Grade)reader.GetInt32(2),
                    reader.IsDBNull(3) ? 0.5 : reader.GetDouble(3), featuresJson));
            }
            observations.Reverse();
        }
        return new RecallTrainingSnapshot(latestLogId, trainedThrough, latestVersion, observations);
    });

    public Task<long> SaveRecallModelProfileAsync(
        int version,
        string weightsJson,
        DateTime trainedAtUtc,
        string metricsJson,
        bool accepted,
        long trainedThroughLogId) => RunAsync(() =>
    {
        using var tx = _conn.BeginTransaction();
        if (accepted)
        {
            using var deactivate = Cmd("UPDATE model_profiles SET is_active=0 WHERE kind='personal-recall-calibrator' AND is_active=1");
            deactivate.Transaction = tx;
            deactivate.ExecuteNonQuery();
        }

        using var insert = Cmd("""
            INSERT INTO model_profiles(kind, version, weights, trained_at, metrics, is_active, accepted, trained_through_log_id)
            VALUES('personal-recall-calibrator', @version, @weights, @trained, @metrics, @active, @accepted, @through)
            """,
            ("@version", version), ("@weights", weightsJson), ("@trained", Fmt(trainedAtUtc)),
            ("@metrics", metricsJson), ("@active", accepted ? 1 : 0), ("@accepted", accepted ? 1 : 0),
            ("@through", trainedThroughLogId));
        insert.Transaction = tx;
        insert.ExecuteNonQuery();
        using var lastId = _conn.CreateCommand();
        lastId.Transaction = tx;
        lastId.CommandText = "SELECT last_insert_rowid()";
        long id = Convert.ToInt64(lastId.ExecuteScalar(), CultureInfo.InvariantCulture);
        tx.Commit();
        return id;
    });

    public Task<InterruptedSessionRecord?> GetInterruptedSessionAsync() => RunAsync(() =>
    {
        long sessionId;
        Intensity intensity;
        DateTime startedAt;
        int plannedCards, newCount, reviews, again, hard, good, easy;
        double estimatedMinutes;
        StudyMode mode;
        using (var cmd = Cmd("""
            SELECT s.id, s.intensity, s.started_at, s.planned_cards, s.new_count, s.estimated_minutes,
                   CASE WHEN EXISTS(SELECT 1 FROM review_logs WHERE session_id=s.id) THEN
                       (SELECT COUNT(*) FROM review_logs WHERE session_id=s.id) ELSE s.reviews END,
                   CASE WHEN EXISTS(SELECT 1 FROM review_logs WHERE session_id=s.id) THEN
                       (SELECT COUNT(*) FROM review_logs WHERE session_id=s.id AND rating=1) ELSE s.again END,
                   CASE WHEN EXISTS(SELECT 1 FROM review_logs WHERE session_id=s.id) THEN
                       (SELECT COUNT(*) FROM review_logs WHERE session_id=s.id AND rating=2) ELSE s.hard END,
                   CASE WHEN EXISTS(SELECT 1 FROM review_logs WHERE session_id=s.id) THEN
                       (SELECT COUNT(*) FROM review_logs WHERE session_id=s.id AND rating=3) ELSE s.good END,
                   CASE WHEN EXISTS(SELECT 1 FROM review_logs WHERE session_id=s.id) THEN
                       (SELECT COUNT(*) FROM review_logs WHERE session_id=s.id AND rating=4) ELSE s.easy END,
                   s.study_mode
            FROM sessions s WHERE s.ended_at IS NULL ORDER BY s.id DESC LIMIT 1
            """))
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read())
                return null;
            sessionId = reader.GetInt64(0);
            intensity = (Intensity)reader.GetInt32(1);
            startedAt = ParseUtc(reader.GetString(2));
            plannedCards = reader.GetInt32(3);
            newCount = reader.GetInt32(4);
            estimatedMinutes = reader.GetDouble(5);
            reviews = reader.GetInt32(6);
            again = reader.GetInt32(7);
            hard = reader.GetInt32(8);
            good = reader.GetInt32(9);
            easy = reader.GetInt32(10);
            mode = (StudyMode)reader.GetInt32(11);
        }

        var queue = new List<SessionQueueCardRecord>();
        using (var cmd = Cmd("""
            SELECT q.id, c.id, c.word_id, c.deck_id, c.state, c.step, c.stability, c.difficulty,
                   c.due, c.last_review, c.reps, c.lapses, c.created_at, q.is_early_review
            FROM session_queue q JOIN cards c ON c.id=q.card_id
            WHERE q.session_id=@session ORDER BY q.position, q.id
            """, ("@session", sessionId)))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                queue.Add(new SessionQueueCardRecord(reader.GetInt64(0), ReadCard(reader, 1), reader.GetInt32(13) != 0));
        }

        return new InterruptedSessionRecord(sessionId, intensity, startedAt, plannedCards, newCount,
            estimatedMinutes, reviews, again, hard, good, easy, queue) { Mode = mode };
    });

    public Task<int> CloseInterruptedSessionsAsync(DateTime endedAtUtc, long? exceptSessionId = null) => RunAsync(() =>
    {
        using var tx = _conn.BeginTransaction();
        using (var deleteQueue = Cmd("""
            DELETE FROM session_queue WHERE session_id IN (
                SELECT id FROM sessions WHERE ended_at IS NULL AND (@except IS NULL OR id<>@except)
            )
            """, ("@except", exceptSessionId)))
        {
            deleteQueue.Transaction = tx;
            deleteQueue.ExecuteNonQuery();
        }
        using var cmd = Cmd("""
            UPDATE sessions SET ended_at=@ended,
                reviews=CASE WHEN EXISTS(SELECT 1 FROM review_logs WHERE session_id=sessions.id)
                    THEN (SELECT COUNT(*) FROM review_logs WHERE session_id=sessions.id) ELSE reviews END,
                again=CASE WHEN EXISTS(SELECT 1 FROM review_logs WHERE session_id=sessions.id)
                    THEN (SELECT COUNT(*) FROM review_logs WHERE session_id=sessions.id AND rating=1) ELSE again END,
                hard=CASE WHEN EXISTS(SELECT 1 FROM review_logs WHERE session_id=sessions.id)
                    THEN (SELECT COUNT(*) FROM review_logs WHERE session_id=sessions.id AND rating=2) ELSE hard END,
                good=CASE WHEN EXISTS(SELECT 1 FROM review_logs WHERE session_id=sessions.id)
                    THEN (SELECT COUNT(*) FROM review_logs WHERE session_id=sessions.id AND rating=3) ELSE good END,
                easy=CASE WHEN EXISTS(SELECT 1 FROM review_logs WHERE session_id=sessions.id)
                    THEN (SELECT COUNT(*) FROM review_logs WHERE session_id=sessions.id AND rating=4) ELSE easy END
            WHERE ended_at IS NULL AND (@except IS NULL OR id<>@except)
            """, ("@ended", Fmt(endedAtUtc)), ("@except", exceptSessionId));
        cmd.Transaction = tx;
        int closed = cmd.ExecuteNonQuery();
        tx.Commit();
        return closed;
    });

    /// <summary>Atomically persist a reviewed card plus its immutable log row.</summary>
    public Task AnswerCardAsync(CardRecord updated, ReviewLogRow log)
        => RunAsync(() => PersistAnswer(updated, log, null, null, false, false).ReviewLogId);

    public Task<long> AnswerCardWithPredictionAsync(CardRecord updated, ReviewLogRow log, RecallPredictionRecord prediction)
        => RunAsync(() => PersistAnswer(updated, log, prediction, null, false, false).ReviewLogId);

    public Task<AnswerPersistenceResult> AnswerSessionCardAsync(
        CardRecord updated,
        ReviewLogRow log,
        RecallPredictionRecord? prediction,
        long sessionQueueEntryId,
        bool requeue,
        bool incorrectInput = false)
        => RunAsync(() => PersistAnswer(updated, log, prediction, sessionQueueEntryId, requeue, incorrectInput));

    private AnswerPersistenceResult PersistAnswer(
        CardRecord updated,
        ReviewLogRow log,
        RecallPredictionRecord? prediction,
        long? sessionQueueEntryId,
        bool requeue,
        bool incorrectInput)
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
                                    fsrs_retrievability, engine_version, next_due, next_interval_days, hint_used, session_id)
            VALUES(@card, @at, @rating, @sb, @spb, @stb, @dfb, @sched, @repsb, @raw, @el, @valid, @r, @ver, @nd, @nid, @hint, @session)
            """,
            ("@card", log.CardId), ("@at", Fmt(log.ReviewedAtUtc)), ("@rating", (int)log.Rating),
            ("@sb", (int)log.StateBefore), ("@spb", log.StepBefore), ("@stb", log.StabilityBefore), ("@dfb", log.DifficultyBefore),
            ("@sched", log.ScheduledDaysBefore), ("@repsb", log.RepsBefore),
            ("@raw", log.RawElapsedMs), ("@el", log.EffectiveElapsedMs), ("@valid", log.TimingValid ? 1 : 0),
            ("@r", log.FsrsRetrievability), ("@ver", log.EngineVersion),
            ("@nd", Fmt(log.NextDueUtc)), ("@nid", log.NextIntervalDays), ("@hint", log.HintUsed ? 1 : 0), ("@session", log.SessionId)))
        {
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }

        long logId;
        using (var cmd = _conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT last_insert_rowid()";
            logId = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        if (prediction is not null)
        {
            using var cmd = Cmd("""
                INSERT INTO model_predictions(review_log_id, profile_id, predicted_recall, created_at,
                                              raw_recall, model_version, features_json)
                VALUES(@log, @profile, @predicted, @created, @raw, @version, @features)
                """,
                ("@log", logId), ("@profile", prediction.ProfileId), ("@predicted", prediction.PredictedRecall),
                ("@created", Fmt(log.ReviewedAtUtc)), ("@raw", prediction.RawRecall),
                ("@version", prediction.ModelVersion), ("@features", prediction.FeaturesJson));
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }

        if (incorrectInput)
            SetCardMarkCore(tx, updated.Id, CardMark.Wrong, enabled: true);

        long? requeuedQueueId = null;
        if (sessionQueueEntryId is not null)
        {
            if (log.SessionId is null)
                throw new InvalidOperationException("A persisted session queue entry requires a session id.");
            using var removeQueueEntry = Cmd("DELETE FROM session_queue WHERE id=@id AND session_id=@session",
                ("@id", sessionQueueEntryId.Value), ("@session", log.SessionId.Value));
            removeQueueEntry.Transaction = tx;
            if (removeQueueEntry.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("The session queue entry was already answered or removed.");

            if (requeue)
            {
                using var addQueueEntry = Cmd("""
                    INSERT INTO session_queue(session_id, card_id, position)
                    VALUES(@session, @card, (SELECT COALESCE(MAX(position)+1, 0) FROM session_queue WHERE session_id=@session))
                    """, ("@session", log.SessionId.Value), ("@card", updated.Id));
                addQueueEntry.Transaction = tx;
                addQueueEntry.ExecuteNonQuery();
                using var lastQueueId = _conn.CreateCommand();
                lastQueueId.Transaction = tx;
                lastQueueId.CommandText = "SELECT last_insert_rowid()";
                requeuedQueueId = Convert.ToInt64(lastQueueId.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }
        tx.Commit();
        return new AnswerPersistenceResult(logId, requeuedQueueId);
    }

    // ---- sessions & plans ----

    public Task<long> StartSessionAsync(Intensity intensity, DateTime startedUtc) => RunAsync(() =>
    {
        Exec("INSERT INTO sessions(started_at, intensity) VALUES(@s, @i)", ("@s", Fmt(startedUtc)), ("@i", (int)intensity));
        return Scalar<long>("SELECT MAX(id) FROM sessions");
    });

    public Task<SessionStartResult> StartSessionWithQueueAsync(
        Intensity intensity,
        DateTime startedUtc,
        IReadOnlyList<CardRecord> cards,
        int newCount,
        double estimatedMinutes,
        IReadOnlySet<long>? earlyReviewCardIds = null,
        StudyMode mode = StudyMode.Recall) => RunAsync(() =>
    {
        using var tx = _conn.BeginTransaction();
        using var insertSession = Cmd("""
            INSERT INTO sessions(started_at, intensity, planned_cards, new_count, estimated_minutes, study_mode)
            VALUES(@started, @intensity, @planned, @new, @minutes, @mode)
            """, ("@started", Fmt(startedUtc)), ("@intensity", (int)intensity),
            ("@planned", cards.Count), ("@new", newCount), ("@minutes", estimatedMinutes), ("@mode", (int)mode));
        insertSession.Transaction = tx;
        insertSession.ExecuteNonQuery();
        long sessionId;
        using (var lastSessionId = _conn.CreateCommand())
        {
            lastSessionId.Transaction = tx;
            lastSessionId.CommandText = "SELECT last_insert_rowid()";
            sessionId = Convert.ToInt64(lastSessionId.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        var queueIds = new List<long>(cards.Count);
        for (int i = 0; i < cards.Count; i++)
        {
            using var insertQueue = Cmd("""
                INSERT INTO session_queue(session_id, card_id, position, is_early_review)
                VALUES(@session, @card, @position, @early)
                """,
                ("@session", sessionId), ("@card", cards[i].Id), ("@position", i),
                ("@early", earlyReviewCardIds?.Contains(cards[i].Id) == true ? 1 : 0));
            insertQueue.Transaction = tx;
            insertQueue.ExecuteNonQuery();
            using var lastQueueId = _conn.CreateCommand();
            lastQueueId.Transaction = tx;
            lastQueueId.CommandText = "SELECT last_insert_rowid()";
            queueIds.Add(Convert.ToInt64(lastQueueId.ExecuteScalar(), CultureInfo.InvariantCulture));
        }
        tx.Commit();
        return new SessionStartResult(sessionId, queueIds);
    });

    public Task FinishSessionAsync(long sessionId, DateTime endedUtc, int reviews, int again, int hard, int good, int easy) => RunAsync(() =>
    {
        using var tx = _conn.BeginTransaction();
        using (var update = Cmd("""
            UPDATE sessions SET ended_at=@e, reviews=@r, again=@a, hard=@h, good=@g, easy=@y WHERE id=@id
            """,
            ("@e", Fmt(endedUtc)), ("@r", reviews), ("@a", again), ("@h", hard), ("@g", good), ("@y", easy), ("@id", sessionId)))
        {
            update.Transaction = tx;
            update.ExecuteNonQuery();
        }
        using (var deleteQueue = Cmd("DELETE FROM session_queue WHERE session_id=@id", ("@id", sessionId)))
        {
            deleteQueue.Transaction = tx;
            deleteQueue.ExecuteNonQuery();
        }
        tx.Commit();
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
