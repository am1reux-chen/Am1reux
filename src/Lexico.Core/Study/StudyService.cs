using Lexico.Core.Data;
using Lexico.Core.Domain;
using Lexico.Core.Scheduling;
using System.Text.Json;

namespace Lexico.Core.Study;

public sealed record SessionPlanInfo(Intensity Intensity, int TotalCards, int NewCount, double EstimatedMinutes);

public sealed record SessionSummary(int Reviews, int Again, int Hard, int Good, int Easy, TimeSpan Duration);

public sealed record StudyAnswerContext(bool HintUsed = false, bool IncorrectInput = false);

/// <summary>
/// Orchestrates one learning session: builds the queue from the daily plan,
/// serves cards, applies FSRS scheduling, persists immutable logs, and requeues
/// short-interval learning steps that come due again within the session window.
/// </summary>
public sealed class StudyService
{
    /// <summary>Learning steps due within this window are served inside the session.</summary>
    private static readonly TimeSpan SessionRequeueWindow = TimeSpan.FromMinutes(30);
    /// <summary>A card not yet due within this slack is still served (avoids idle stalls).</summary>
    private static readonly TimeSpan DueSlack = TimeSpan.FromSeconds(90);

    /// <summary>Public view of the near-due slack for UI countdown decisions.</summary>
    public static TimeSpan DueSlackWindow => DueSlack;

    private readonly AppDatabase _db;
    private ISrsEngine _engine;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly SemaphoreSlim _answerGate = new(1, 1);

    private sealed class QueueEntry
    {
        public long SessionQueueEntryId { get; set; }
        public required CardRecord Card { get; set; }
        public required StudyItem Item { get; init; }
        public bool AllowEarlyReview { get; init; }
    }

    private readonly List<QueueEntry> _queue = [];
    private long? _sessionId;
    private DateTime _startedUtc;
    private DateTime _cutoffUtc;
    private int _reviews, _again, _hard, _good, _easy;
    private EffectiveTiming? _currentTiming;
    private long? _currentTimingCardId;
    private long? _revealedCardId;
    private long? _pausedAtTimestamp;
    private bool _isPaused;
    private RecallModelProfileRecord? _recallProfile;

    public StudyService(AppDatabase db, ISrsEngine engine, TimeProvider? timeProvider = null)
    {
        _db = db;
        _engine = engine;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public SessionPlanInfo? CurrentPlan { get; private set; }

    public StudyMode CurrentMode { get; private set; } = StudyMode.Recall;

    public bool IsActive => _sessionId is not null;

    /// <summary>Number of review records committed in the active or most recently finished session.</summary>
    public int CompletedReviews => _reviews;

    /// <summary>True only while an active session is explicitly paused in this process.</summary>
    public bool IsPaused => _isPaused;

    /// <summary>Queue entries still in the session, including the card currently being shown.</summary>
    public int Remaining => _queue.Count;

    /// <summary>Wait for session and answer mutations already in progress without ending the session.</summary>
    public async Task WaitForPendingOperationsAsync()
    {
        await _sessionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _answerGate.WaitAsync().ConfigureAwait(false);
            _answerGate.Release();
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    /// <summary>Pause without ending the session or changing its queue or saved reviews.</summary>
    public async Task PauseAsync()
    {
        await _answerGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_sessionId is null || _isPaused)
                return;
            _isPaused = true;
            _pausedAtTimestamp = _timeProvider.GetTimestamp();
        }
        finally
        {
            _answerGate.Release();
        }
    }

    /// <summary>Resume an explicitly paused session and exclude paused wall time from the active card timer.</summary>
    public async Task ResumeAsync()
    {
        await _answerGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_sessionId is null || !_isPaused)
                return;
            if (_pausedAtTimestamp is { } pausedAt)
                _currentTiming?.AddUnfocused(_timeProvider.GetElapsedTime(pausedAt).TotalMilliseconds);
            _pausedAtTimestamp = null;
            _isPaused = false;
        }
        finally
        {
            _answerGate.Release();
        }
    }

    /// <summary>Remember that the active card's answer is visible across Learn ViewModel recreation.</summary>
    public void RevealCard(long cardId)
    {
        if (_sessionId is not null && !_isPaused && _queue.Count > 0 && _queue[0].Card.Id == cardId)
            _revealedCardId = cardId;
    }

    /// <summary>Whether the specified active queue card was revealed and remains ungraded.</summary>
    public bool IsCardRevealed(long cardId) =>
        _revealedCardId == cardId && _queue.Count > 0 && _queue[0].Card.Id == cardId;

    /// <summary>Builds all intensity previews from one candidate and timing snapshot.</summary>
    public async Task<IReadOnlyDictionary<Intensity, DailyPlanner.Plan>> BuildPlansAsync(DateTime nowUtc, long? deckId = null)
    {
        var now = AsUtc(nowUtc);
        var (candidates, baseline) = await LoadPlanningInputsAsync(now, deckId).ConfigureAwait(false);
        return Enum.GetValues<Intensity>().ToDictionary(
            intensity => intensity,
            intensity => DailyPlanner.Build(intensity, candidates, baseline, now));
    }

    /// <summary>Build today's queue for the chosen intensity. Falls back gracefully when nothing is due.</summary>
    public Task<SessionPlanInfo> StartAsync(Intensity intensity, long? deckId = null)
        => StartAsync(intensity, StudyMode.Recall, deckId);

    public async Task<SessionPlanInfo> StartAsync(Intensity intensity, StudyMode mode, long? deckId = null)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown study mode.");

        await _sessionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_sessionId is not null && CurrentPlan is not null)
                return CurrentPlan;

            if (await RecoverInterruptedSessionCoreAsync().ConfigureAwait(false) > 0)
                return CurrentPlan!;
            await TrainRecallCalibratorIfReadyAsync().ConfigureAwait(false);
            _recallProfile = await _db.GetActiveRecallModelProfileAsync().ConfigureAwait(false);

            var now = DateTime.UtcNow;
            var plan = await BuildPlanAsync(intensity, now, deckId).ConfigureAwait(false);
            var selected = plan.DueCards.Concat(plan.EarlyReviewCards).Concat(plan.NewCards)
                .Select(candidate => candidate.Card)
                .DistinctBy(card => card.Id)
                .ToList();
            var earlyReviewIds = plan.EarlyReviewCards.Select(candidate => candidate.Card.Id).ToHashSet();

            var wordIds = selected.Select(card => card.WordId).Distinct().ToList();
            var words = (await _db.LoadWordsWithSenses(wordIds).ConfigureAwait(false)).ToDictionary(word => word.Id);

            _queue.Clear();
            foreach (var card in selected)
            {
                if (!words.TryGetValue(card.WordId, out var word))
                    continue;
                _queue.Add(new QueueEntry
                {
                    Card = card,
                    Item = ToItem(card, word),
                    AllowEarlyReview = earlyReviewIds.Contains(card.Id),
                });
            }

            _startedUtc = now;
            _cutoffUtc = now.Add(SessionRequeueWindow);
            _reviews = _again = _hard = _good = _easy = 0;
            _isPaused = false;
            _pausedAtTimestamp = null;
            _currentTiming = null;
            _currentTimingCardId = null;
            _revealedCardId = null;
            var started = await _db.StartSessionWithQueueAsync(
                intensity, now, _queue.Select(entry => entry.Card).ToArray(), plan.NewCount, plan.EstimatedMinutes,
                earlyReviewIds, mode)
                .ConfigureAwait(false);
            _sessionId = started.SessionId;
            CurrentMode = mode;
            for (int i = 0; i < _queue.Count; i++)
                _queue[i].SessionQueueEntryId = started.QueueEntryIds[i];
            CurrentPlan = new SessionPlanInfo(intensity, _queue.Count, plan.NewCount, plan.EstimatedMinutes);

            await _db.SaveDailyPlanAsync(
                now.ToLocalTime().ToString("yyyy-MM-dd"), intensity,
                plan.ReviewCount + plan.EarlyReviewCount, plan.NewCount, plan.EstimatedMinutes).ConfigureAwait(false);
            return CurrentPlan;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    /// <summary>Next card that is due (or nearly due). Null when the session is finished or resting.</summary>
    public StudyItem? PeekNext()
    {
        if (_isPaused)
            return null;

        var now = DateTime.UtcNow;
        for (int i = 0; i < _queue.Count; i++)
        {
            if (_queue[i].AllowEarlyReview || _queue[i].Card.DueUtc <= now.Add(DueSlack))
            {
                var entry = _queue[i];
                _queue.RemoveAt(i);
                _queue.Insert(0, entry);
                if (_currentTimingCardId != entry.Card.Id)
                    _revealedCardId = null;
                StartTiming(entry.Card.Id);
                return entry.Item;
            }
        }
        return null;
    }

    /// <summary>Next due time across the remaining queue; null when empty.</summary>
    public DateTime? NextDueUtc() => _queue.Count == 0
        ? null
        : _queue.Any(entry => entry.AllowEarlyReview)
            ? DateTime.UtcNow
            : _queue.Min(e => e.Card.DueUtc);

    private void StartTiming(long cardId)
    {
        if (_currentTiming is not null && _currentTimingCardId == cardId)
            return;
        _currentTiming = new EffectiveTiming(_timeProvider);
        _currentTimingCardId = cardId;
    }

    /// <summary>Called by the UI when focus is lost mid-card.</summary>
    public void NotifyUnfocused(double unfocusedMs) => _currentTiming?.AddUnfocused(unfocusedMs);

    /// <summary>
    /// Grade the current card: schedule with FSRS, persist card + immutable log,
    /// requeue in-session learning steps. Returns the requeued card's due time.
    /// </summary>
    public Task<DateTime?> AnswerAsync(StudyItem item, Grade grade)
        => AnswerAsync(item, grade, new StudyAnswerContext());

    /// <summary>Answer with additive study context used only for shadow recall evaluation.</summary>
    public async Task<DateTime?> AnswerAsync(StudyItem item, Grade grade, StudyAnswerContext context)
    {
        await _answerGate.WaitAsync().ConfigureAwait(false);
        try { return await AnswerCoreAsync(item, grade, context).ConfigureAwait(false); }
        finally { _answerGate.Release(); }
    }

    private async Task<DateTime?> AnswerCoreAsync(StudyItem item, Grade grade, StudyAnswerContext context)
    {
        if (_isPaused)
            return null;

        var entry = _queue.FirstOrDefault(e => e.Card.Id == item.CardId && e.Item == item);
        if (entry is null)
            return null;

        var now = DateTime.UtcNow;
        var timing = _currentTimingCardId == item.CardId
            ? _currentTiming?.Finish() ?? TimingResult.Invalid
            : TimingResult.Invalid;
        _currentTiming = null;
        _currentTimingCardId = null;

        var before = entry.Card;
        var effectiveGrade = context.IncorrectInput ? Grade.Again : grade;
        var snapshot = new SrsCardSnapshot(before.State, before.Step, before.Stability, before.Difficulty, before.DueUtc, before.LastReviewUtc);
        var outcome = _engine.Schedule(snapshot, effectiveGrade, now, timing.Valid ? (int)timing.EffectiveMs : null);

        var updatedCard = before with
        {
            State = outcome.UpdatedCard.State,
            Step = outcome.UpdatedCard.Step,
            Stability = outcome.UpdatedCard.Stability,
            Difficulty = outcome.UpdatedCard.Difficulty,
            DueUtc = outcome.UpdatedCard.DueUtc,
            LastReviewUtc = now,
            Reps = before.Reps + 1,
            Lapses = before.Lapses + (effectiveGrade == Grade.Again && before.State == CardState.Review ? 1 : 0),
        };

        var scheduledDaysBefore = before.LastReviewUtc is null ? 0 : Math.Max(0, (now - before.LastReviewUtc.Value).TotalDays);
        var history = await _db.GetRecentCardReviewHistoryAsync(before.Id).ConfigureAwait(false);
        var features = PersonalRecallCalibrator.BuildFeatures(
            outcome.RetrievabilityAtReview,
            before.State,
            scheduledDaysBefore,
            timing.EffectiveMs,
            timing.Valid,
            context.HintUsed,
            new PersonalRecallCalibrator.PriorHistory(
                history.Select(sample => sample.Rating).ToArray(),
                history.Select(sample => (sample.EffectiveElapsedMs, sample.TimingValid)).ToArray(),
                before.Lapses));

        var log = new ReviewLogRow(
            Id: 0,
            CardId: before.Id,
            ReviewedAtUtc: now,
            Rating: effectiveGrade,
            StateBefore: before.State,
            StepBefore: before.Step,
            StabilityBefore: before.Stability,
            DifficultyBefore: before.Difficulty,
            ScheduledDaysBefore: Math.Round(scheduledDaysBefore, 4),
            RepsBefore: before.Reps,
            RawElapsedMs: timing.RawMs,
            EffectiveElapsedMs: timing.EffectiveMs,
            TimingValid: timing.Valid,
            FsrsRetrievability: outcome.RetrievabilityAtReview > 0 ? outcome.RetrievabilityAtReview : null,
            EngineVersion: _engine.Version,
            NextDueUtc: updatedCard.DueUtc,
            NextIntervalDays: Math.Round(outcome.NextIntervalDays, 4))
        {
            HintUsed = context.HintUsed,
            SessionId = _sessionId,
        };

        bool shortStep = outcome.UpdatedCard.State is CardState.Learning or CardState.Relearning;
        bool requeue = shortStep && updatedCard.DueUtc <= _cutoffUtc;
        AnswerPersistenceResult persisted;
        if (_recallProfile is { } profile)
        {
            var weights = ReadWeights(profile.WeightsJson);
            double prediction = PersonalRecallCalibrator.Predict(
                outcome.RetrievabilityAtReview, features, weights, profile.Accepted);
            persisted = await _db.AnswerSessionCardAsync(updatedCard, log, new RecallPredictionRecord(
                profile.Id, profile.Version, outcome.RetrievabilityAtReview, prediction,
                JsonSerializer.Serialize(features)), entry.SessionQueueEntryId, requeue, context.IncorrectInput).ConfigureAwait(false);
        }
        else
        {
            persisted = await _db.AnswerSessionCardAsync(updatedCard, log, null,
                entry.SessionQueueEntryId, requeue, context.IncorrectInput).ConfigureAwait(false);
        }
        entry.Card = updatedCard;
        _queue.Remove(entry);
        _reviews++;
        _revealedCardId = null;
        _ = effectiveGrade switch
        {
            Grade.Again => _again++,
            Grade.Hard => _hard++,
            Grade.Good => _good++,
            Grade.Easy => _easy++,
            _ => 0,
        };

        DateTime? requeuedAt = null;
        if (requeue)
        {
            _queue.Add(new QueueEntry
            {
                SessionQueueEntryId = persisted.RequeuedQueueEntryId!.Value,
                Card = updatedCard,
                Item = entry.Item with { IsNew = false, IsRelearning = updatedCard.State == CardState.Relearning, DueUtc = updatedCard.DueUtc },
            });
            requeuedAt = updatedCard.DueUtc;
        }
        return requeuedAt;
    }

    private async Task TrainRecallCalibratorIfReadyAsync()
    {
        var snapshot = await _db.GetRecallTrainingSnapshotAsync().ConfigureAwait(false);
        if (snapshot.LatestReviewLogId - snapshot.LastTrainedThroughLogId < 50 ||
            snapshot.Observations.Count < PersonalRecallCalibrator.MinimumTrainingExamples)
            return;

        var examples = new List<PersonalRecallCalibrator.TrainingExample>(snapshot.Observations.Count);
        foreach (var observation in snapshot.Observations)
        {
            try
            {
                var features = JsonSerializer.Deserialize<PersonalRecallCalibrator.FeatureVector>(observation.FeaturesJson);
                if (features is null)
                    continue;
                examples.Add(new PersonalRecallCalibrator.TrainingExample(
                    observation.ReviewLogId, observation.ReviewedAtUtc,
                    observation.Rating != Grade.Again, features));
            }
            catch (JsonException)
            {
                // Ignore malformed historical feature rows; immutable review logs remain intact.
            }
        }

        var result = PersonalRecallCalibrator.Train(examples);
        if (!result.Eligible)
            return;

        var metrics = JsonSerializer.Serialize(new
        {
            result.Accepted,
            result.TrainingCount,
            result.ValidationCount,
            result.TrainingThroughUtc,
            result.ValidationStartUtc,
            result.BaselineMetrics,
            result.CalibratedMetrics,
        });
        await _db.SaveRecallModelProfileAsync(
            snapshot.LatestModelVersion + 1,
            JsonSerializer.Serialize(result.Weights),
            DateTime.UtcNow,
            metrics,
            result.Accepted,
            snapshot.LatestReviewLogId).ConfigureAwait(false);
    }

    private static double[]? ReadWeights(string weightsJson)
    {
        try { return JsonSerializer.Deserialize<double[]>(weightsJson); }
        catch (JsonException) { return null; }
    }

    public async Task<int> RecoverInterruptedSessionsAsync()
    {
        await _sessionGate.WaitAsync().ConfigureAwait(false);
        try { return await RecoverInterruptedSessionCoreAsync().ConfigureAwait(false); }
        finally { _sessionGate.Release(); }
    }

    /// <summary>Discard live session state and reload any saved session after the database is restored.</summary>
    public async Task ReloadAfterRestoreAsync(ISrsEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        await _sessionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _answerGate.WaitAsync().ConfigureAwait(false);
            try
            {
                _sessionId = null;
                _startedUtc = default;
                _cutoffUtc = default;
                _reviews = _again = _hard = _good = _easy = 0;
                CurrentPlan = null;
                _queue.Clear();
                _engine = engine;
                _currentTiming = null;
                _currentTimingCardId = null;
                _revealedCardId = null;
                _pausedAtTimestamp = null;
                _isPaused = false;
                _recallProfile = null;
                await RecoverInterruptedSessionCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                _answerGate.Release();
            }
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private async Task<int> RecoverInterruptedSessionCoreAsync()
    {
        if (_sessionId is not null)
            return 0;

        _isPaused = false;
        _pausedAtTimestamp = null;
        _revealedCardId = null;
        _currentTiming = null;
        _currentTimingCardId = null;

        var snapshot = await _db.GetInterruptedSessionAsync().ConfigureAwait(false);
        if (snapshot is null)
        {
            await _db.CloseInterruptedSessionsAsync(DateTime.UtcNow).ConfigureAwait(false);
            return 0;
        }

        await _db.CloseInterruptedSessionsAsync(DateTime.UtcNow, snapshot.SessionId).ConfigureAwait(false);
        if (snapshot.Queue.Count == 0)
        {
            await _db.CloseInterruptedSessionsAsync(DateTime.UtcNow).ConfigureAwait(false);
            return 0;
        }

        var words = (await _db.LoadWordsWithSenses(snapshot.Queue.Select(entry => entry.Card.WordId).Distinct().ToArray())
            .ConfigureAwait(false)).ToDictionary(word => word.Id);
        _queue.Clear();
        foreach (var queued in snapshot.Queue)
        {
            if (words.TryGetValue(queued.Card.WordId, out var word))
            {
                _queue.Add(new QueueEntry
                {
                    SessionQueueEntryId = queued.QueueEntryId,
                    Card = queued.Card,
                    Item = ToItem(queued.Card, word),
                    AllowEarlyReview = queued.IsEarlyReview,
                });
            }
        }

        if (_queue.Count == 0)
        {
            await _db.CloseInterruptedSessionsAsync(DateTime.UtcNow).ConfigureAwait(false);
            return 0;
        }

        _sessionId = snapshot.SessionId;
        _startedUtc = snapshot.StartedAtUtc;
        _cutoffUtc = DateTime.UtcNow.Add(SessionRequeueWindow);
        _reviews = snapshot.Reviews;
        _again = snapshot.Again;
        _hard = snapshot.Hard;
        _good = snapshot.Good;
        _easy = snapshot.Easy;
        CurrentPlan = new SessionPlanInfo(
            snapshot.Intensity,
            Math.Max(snapshot.PlannedCards, _queue.Count),
            Math.Clamp(snapshot.NewCount, 0, Math.Max(snapshot.PlannedCards, _queue.Count)),
            double.IsFinite(snapshot.EstimatedMinutes) ? Math.Max(0, snapshot.EstimatedMinutes) : 0);
        _recallProfile = await _db.GetActiveRecallModelProfileAsync().ConfigureAwait(false);
        CurrentMode = snapshot.Mode;
        return 1;
    }

    public async Task<SessionSummary> FinishAsync()
    {
        await _sessionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _answerGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_sessionId is null)
                    return new SessionSummary(_reviews, _again, _hard, _good, _easy, TimeSpan.Zero);
                var end = DateTime.UtcNow;
                await _db.FinishSessionAsync(_sessionId.Value, end, _reviews, _again, _hard, _good, _easy).ConfigureAwait(false);
                _sessionId = null;
                _queue.Clear();
                _isPaused = false;
                _pausedAtTimestamp = null;
                _revealedCardId = null;
                _currentTiming = null;
                _currentTimingCardId = null;
                return new SessionSummary(_reviews, _again, _hard, _good, _easy, end - _startedUtc);
            }
            finally
            {
                _answerGate.Release();
            }
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private async Task<DailyPlanner.Plan> BuildPlanAsync(Intensity intensity, DateTime nowUtc, long? deckId)
    {
        var (candidates, baseline) = await LoadPlanningInputsAsync(nowUtc, deckId).ConfigureAwait(false);
        return DailyPlanner.Build(intensity, candidates, baseline, nowUtc);
    }

    private async Task<(DailyPlanner.Candidate[] Candidates, DailyPlanner.Baselines Baselines)> LoadPlanningInputsAsync(
        DateTime nowUtc,
        long? deckId)
    {
        var records = await _db.GetPlanningCardsAsync(nowUtc, nowUtc.Add(DailyPlanner.HeavyEarlyHorizon), deckId)
            .ConfigureAwait(false);
        var baseline = await DailyPlanner.LoadBaselinesAsync(_db).ConfigureAwait(false);
        var candidates = records.Select(record => new DailyPlanner.Candidate(
            record.Card,
            record.FrequencyRank,
            _engine.PredictRecall(ToSnapshot(record.Card), nowUtc))).ToArray();
        return (candidates, baseline);
    }

    private static SrsCardSnapshot ToSnapshot(CardRecord card) => new(
        card.State, card.Step, card.Stability, card.Difficulty, card.DueUtc, card.LastReviewUtc);

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static StudyItem ToItem(CardRecord card, WordEntry word) => new(
        card.Id, word.Lemma, word.IpaUs, word.IpaUk, word.Senses,
        IsNew: card.Reps == 0,
        IsRelearning: card.State == CardState.Relearning,
        Stability: card.Stability,
        DueUtc: card.DueUtc)
    {
        Details = word.Details,
    };
}
