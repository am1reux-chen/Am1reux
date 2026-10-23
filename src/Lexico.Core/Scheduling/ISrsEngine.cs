using Lexico.Core.Domain;

namespace Lexico.Core.Scheduling;

/// <summary>Engine-agnostic snapshot of a card's scheduling state.</summary>
public sealed record SrsCardSnapshot(
    Domain.CardState State,
    int? Step,
    double? Stability,
    double? Difficulty,
    DateTime DueUtc,
    DateTime? LastReviewUtc);

/// <summary>Result of scheduling one review.</summary>
public sealed record SchedulingOutcome(
    SrsCardSnapshot UpdatedCard,
    double RetrievabilityAtReview,
    double NextIntervalDays,
    bool WasRelearned);

/// <summary>
/// The single seam between Lexio and any spaced-repetition engine.
/// Implementations must be deterministic given the same inputs (disable fuzzing,
/// or seed it) so that logs stay reproducible.
/// </summary>
public interface ISrsEngine
{
    string Version { get; }

    double DesiredRetention { get; }

    /// <summary>Predicted recall probability in [0,1] at <paramref name="nowUtc"/>; 0 when never reviewed.</summary>
    double PredictRecall(SrsCardSnapshot card, DateTime nowUtc);

    SchedulingOutcome Schedule(SrsCardSnapshot card, Grade grade, DateTime nowUtc, int? elapsedMs = null);
}

/// <summary>FSRS-6 via FSRS.Core 1.0.7, with py-fsrs 6.3.2 conformance patches (MIT).</summary>
public sealed class FsrsCoreEngine : ISrsEngine
{
    public const string EngineVersion = "fsrs-core/1.0.7+lexio-fsrs6fix1";

    private readonly FSRS.Core.Interfaces.IScheduler _scheduler;

    public FsrsCoreEngine(double desiredRetention = 0.9, bool enableFuzzing = true)
    {
        _scheduler = new FSRS.Core.Services.Scheduler(
            desiredRetention: desiredRetention,
            enableFuzzing: enableFuzzing,
            retrievabilityCalculator: new ReferenceRetrievabilityCalculator(),
            stabilityCalculator: new ReferenceStabilityCalculator());
    }

    public string Version => EngineVersion;

    public double DesiredRetention => _scheduler.DesiredRetention;

    public double PredictRecall(SrsCardSnapshot card, DateTime nowUtc)
    {
        var fsrsCard = ToFsrsCard(card);
        if (fsrsCard.LastReview is null)
            return 0;
        return _scheduler.GetCardRetrievability(fsrsCard, nowUtc);
    }

    public SchedulingOutcome Schedule(SrsCardSnapshot card, Grade grade, DateTime nowUtc, int? elapsedMs = null)
    {
        var fsrsCard = ToFsrsCard(card);
        double retrievability = fsrsCard.LastReview is null ? 0 : _scheduler.GetCardRetrievability(fsrsCard, nowUtc);
        var (updated, _) = _scheduler.ReviewCard(fsrsCard, ToFsrsRating(grade), nowUtc, elapsedMs);
        var intervalDays = Math.Max(0, (updated.Due - nowUtc).TotalDays);
        return new SchedulingOutcome(
            FromFsrsCard(updated),
            retrievability,
            intervalDays,
            WasRelearned: card.State == Domain.CardState.Relearning && updated.State != FSRS.Core.Enums.State.Relearning);
    }

    private static FSRS.Core.Models.Card ToFsrsCard(SrsCardSnapshot card) => new(
        cardId: Guid.NewGuid(),
        state: ToFsrsState(card.State),
        step: card.Step,
        stability: card.Stability,
        difficulty: card.Difficulty,
        due: card.DueUtc,
        lastReview: card.LastReviewUtc);

    private static SrsCardSnapshot FromFsrsCard(FSRS.Core.Models.Card c) => new(
        FromFsrsState(c.State), c.Step, c.Stability, c.Difficulty, c.Due, c.LastReview);

    private static FSRS.Core.Enums.Rating ToFsrsRating(Grade g) => g switch
    {
        Grade.Again => FSRS.Core.Enums.Rating.Again,
        Grade.Hard => FSRS.Core.Enums.Rating.Hard,
        Grade.Good => FSRS.Core.Enums.Rating.Good,
        Grade.Easy => FSRS.Core.Enums.Rating.Easy,
        _ => throw new ArgumentOutOfRangeException(nameof(g), g, null),
    };

    private static FSRS.Core.Enums.State ToFsrsState(Domain.CardState s) => s switch
    {
        Domain.CardState.Learning => FSRS.Core.Enums.State.Learning,
        Domain.CardState.Review => FSRS.Core.Enums.State.Review,
        Domain.CardState.Relearning => FSRS.Core.Enums.State.Relearning,
        _ => throw new ArgumentOutOfRangeException(nameof(s), s, null),
    };

    private static Domain.CardState FromFsrsState(FSRS.Core.Enums.State s) => s switch
    {
        FSRS.Core.Enums.State.Learning => Domain.CardState.Learning,
        FSRS.Core.Enums.State.Review => Domain.CardState.Review,
        FSRS.Core.Enums.State.Relearning => Domain.CardState.Relearning,
        _ => throw new ArgumentOutOfRangeException(nameof(s), s, null),
    };

    /// <summary>FSRS uses whole elapsed days for retrievability, including fractional-day reviews.</summary>
    private sealed class ReferenceRetrievabilityCalculator : FSRS.Core.Interfaces.IRetrievabilityCalculator
    {
        public double CalculateRetrievability(
            FSRS.Core.Models.Card card,
            DateTime? currentDateTime = null,
            double[]? parameters = null)
        {
            if (card.LastReview is null || card.Stability is null)
                return 0;

            var now = currentDateTime ?? DateTime.UtcNow;
            var weights = parameters ?? FSRS.Core.Constants.FsrsConstants.DefaultParameters;
            var elapsedDays = Math.Max(0, (now - card.LastReview.Value).Days);
            var decay = -weights[20];
            var factor = Math.Pow(0.9, 1.0 / decay) - 1;
            return Math.Pow(1 + factor * elapsedDays / card.Stability.Value, decay);
        }
    }

    /// <summary>py-fsrs 6.3.2 floors same-day stability growth at 1 for Hard, Good, and Easy.</summary>
    private sealed class ReferenceStabilityCalculator : FSRS.Core.Interfaces.IStabilityCalculator
    {
        private readonly FSRS.Core.Services.StabilityCalculator _inner = new();

        public double CalculateInitialStability(FSRS.Core.Enums.Rating rating, double[] parameters)
            => _inner.CalculateInitialStability(rating, parameters);

        public double CalculateShortTermStability(double stability, FSRS.Core.Enums.Rating rating, double[] parameters)
        {
            var updated = _inner.CalculateShortTermStability(stability, rating, parameters);
            return rating is FSRS.Core.Enums.Rating.Hard or FSRS.Core.Enums.Rating.Good or FSRS.Core.Enums.Rating.Easy
                ? Math.Max(stability, updated)
                : updated;
        }

        public double CalculateNextStability(
            double difficulty,
            double stability,
            double retrievability,
            FSRS.Core.Enums.Rating rating,
            double[] parameters)
            => _inner.CalculateNextStability(difficulty, stability, retrievability, rating, parameters);
    }
}
