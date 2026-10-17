using Lexico.Core.Data;
using Lexico.Core.Domain;
using Lexico.Core.Scheduling;

namespace Lexico.Core.Study;

public sealed record SessionPlanInfo(Intensity Intensity, int TotalCards, int NewCount, double EstimatedMinutes);

public sealed record SessionSummary(int Reviews, int Again, int Hard, int Good, int Easy, TimeSpan Duration);

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
    private readonly ISrsEngine _engine;

    private sealed class QueueEntry
    {
        public required CardRecord Card { get; set; }
        public required StudyItem Item { get; init; }
    }

    private readonly List<QueueEntry> _queue = [];
    private long? _sessionId;
    private DateTime _startedUtc;
    private DateTime _cutoffUtc;
    private int _reviews, _again, _hard, _good, _easy;
    private EffectiveTiming? _currentTiming;

    public StudyService(AppDatabase db, ISrsEngine engine)
    {
        _db = db;
        _engine = engine;
    }

    public SessionPlanInfo? CurrentPlan { get; private set; }

    public bool IsActive => _sessionId is not null;

    public int Remaining => _queue.Count;

    /// <summary>Build today's queue for the chosen intensity. Falls back gracefully when nothing is due.</summary>
    public async Task<SessionPlanInfo> StartAsync(Intensity intensity, long? deckId = null)
    {
        var now = DateTime.UtcNow;
        var due = await _db.GetDueSnapshotAsync(now).ConfigureAwait(false);
        var baseline = await DailyPlanner.LoadBaselinesAsync(_db).ConfigureAwait(false);
        var plan = DailyPlanner.Build(intensity, due, baseline);

        var cards = new List<CardRecord>();
        cards.AddRange(await _db.GetDueLearningCardsAsync(now, plan.ReviewCount).ConfigureAwait(false));
        int reviewSlots = Math.Max(0, plan.ReviewCount - cards.Count);
        cards.AddRange(await _db.GetDueReviewCardsAsync(now, reviewSlots).ConfigureAwait(false));
        if (plan.EarlyReviewCount > 0)
        {
            cards.AddRange(await _db.GetDueReviewCardsAsync(
                now.Add(DailyPlanner.HeavyEarlyHorizon), plan.EarlyReviewCount, minDueUtc: now).ConfigureAwait(false));
        }
        var newCards = await _db.GetNewCardsAsync(plan.NewCount, deckId).ConfigureAwait(false);
        cards.AddRange(newCards);

        var seen = new HashSet<long>();
        var selected = cards.Where(c => seen.Add(c.Id)).ToList();

        var wordIds = selected.Select(c => c.WordId).Distinct().ToList();
        var words = (await _db.LoadWordsWithSenses(wordIds).ConfigureAwait(false)).ToDictionary(w => w.Id);

        _queue.Clear();
        foreach (var card in selected)
        {
            if (!words.TryGetValue(card.WordId, out var word))
                continue;
            _queue.Add(new QueueEntry
            {
                Card = card,
                Item = ToItem(card, word),
            });
        }

        _startedUtc = now;
        _cutoffUtc = now.Add(SessionRequeueWindow);
        _reviews = _again = _hard = _good = _easy = 0;
        _sessionId = await _db.StartSessionAsync(intensity, now).ConfigureAwait(false);
        CurrentPlan = new SessionPlanInfo(intensity, _queue.Count, newCards.Count, plan.EstimatedMinutes);

        await _db.SaveDailyPlanAsync(
            now.ToLocalTime().ToString("yyyy-MM-dd"), intensity,
            plan.ReviewCount + plan.EarlyReviewCount, plan.NewCount, plan.EstimatedMinutes).ConfigureAwait(false);
        return CurrentPlan;
    }

    /// <summary>Next card that is due (or nearly due). Null when the session is finished or resting.</summary>
    public StudyItem? PeekNext()
    {
        var now = DateTime.UtcNow;
        for (int i = 0; i < _queue.Count; i++)
        {
            if (_queue[i].Card.DueUtc <= now.Add(DueSlack))
            {
                var entry = _queue[i];
                _queue.RemoveAt(i);
                _queue.Insert(0, entry);
                StartTiming();
                return entry.Item;
            }
        }
        return null;
    }

    /// <summary>Next due time across the remaining queue; null when empty.</summary>
    public DateTime? NextDueUtc() => _queue.Count == 0
        ? null
        : _queue.Min(e => e.Card.DueUtc);

    private void StartTiming() => _currentTiming = new EffectiveTiming();

    /// <summary>Called by the UI when focus is lost mid-card.</summary>
    public void NotifyUnfocused(double unfocusedMs) => _currentTiming?.AddUnfocused(unfocusedMs);

    /// <summary>
    /// Grade the current card: schedule with FSRS, persist card + immutable log,
    /// requeue in-session learning steps. Returns the requeued card's due time.
    /// </summary>
    public async Task<DateTime?> AnswerAsync(StudyItem item, Grade grade)
    {
        var now = DateTime.UtcNow;
        var timing = _currentTiming?.Finish() ?? TimingResult.Invalid;
        _currentTiming = null;

        var entry = _queue.FirstOrDefault(e => e.Card.Id == item.CardId);
        if (entry is null)
            return null;

        var before = entry.Card;
        var snapshot = new SrsCardSnapshot(before.State, before.Step, before.Stability, before.Difficulty, before.DueUtc, before.LastReviewUtc);
        var outcome = _engine.Schedule(snapshot, grade, now, timing.Valid ? (int)timing.EffectiveMs : null);

        var updatedCard = before with
        {
            State = outcome.UpdatedCard.State,
            Step = outcome.UpdatedCard.Step,
            Stability = outcome.UpdatedCard.Stability,
            Difficulty = outcome.UpdatedCard.Difficulty,
            DueUtc = outcome.UpdatedCard.DueUtc,
            LastReviewUtc = now,
            Reps = before.Reps + 1,
            Lapses = before.Lapses + (grade == Grade.Again && before.State == CardState.Review ? 1 : 0),
        };

        var scheduledDaysBefore = before.LastReviewUtc is null ? 0 : Math.Max(0, (now - before.LastReviewUtc.Value).TotalDays);
        var log = new ReviewLogRow(
            Id: 0,
            CardId: before.Id,
            ReviewedAtUtc: now,
            Rating: grade,
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
            NextIntervalDays: Math.Round(outcome.NextIntervalDays, 4));

        await _db.AnswerCardAsync(updatedCard, log).ConfigureAwait(false);
        entry.Card = updatedCard;
        _queue.Remove(entry);
        _reviews++;
        _ = grade switch
        {
            Grade.Again => _again++,
            Grade.Hard => _hard++,
            Grade.Good => _good++,
            Grade.Easy => _easy++,
            _ => 0,
        };

        DateTime? requeuedAt = null;
        bool shortStep = outcome.UpdatedCard.State is CardState.Learning or CardState.Relearning;
        if (shortStep && updatedCard.DueUtc <= _cutoffUtc)
        {
            _queue.Add(new QueueEntry { Card = updatedCard, Item = entry.Item with { IsNew = false, IsRelearning = updatedCard.State == CardState.Relearning, DueUtc = updatedCard.DueUtc } });
            requeuedAt = updatedCard.DueUtc;
        }
        return requeuedAt;
    }

    public async Task<SessionSummary> FinishAsync()
    {
        if (_sessionId is null)
            return new SessionSummary(_reviews, _again, _hard, _good, _easy, TimeSpan.Zero);
        var end = DateTime.UtcNow;
        await _db.FinishSessionAsync(_sessionId.Value, end, _reviews, _again, _hard, _good, _easy).ConfigureAwait(false);
        _sessionId = null;
        _queue.Clear();
        return new SessionSummary(_reviews, _again, _hard, _good, _easy, end - _startedUtc);
    }

    private static StudyItem ToItem(CardRecord card, WordEntry word) => new(
        card.Id, word.Lemma, word.IpaUs, word.IpaUk, word.Senses,
        IsNew: card.Reps == 0,
        IsRelearning: card.State == CardState.Relearning,
        Stability: card.Stability,
        DueUtc: card.DueUtc);
}
