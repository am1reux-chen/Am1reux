using Lexico.Core.Data;
using Lexico.Core.Domain;

namespace Lexico.Core.Study;

/// <summary>Builds a time-bounded daily queue without changing card due dates.</summary>
public static class DailyPlanner
{
    public const double LightFactor = 0.55;
    public const double NormalFactor = 1.0;
    public const double HeavyFactor = 1.7;
    public const int DefaultBaselineReviews = 60;
    public const int NewCardLimitLight = 10;
    public const int NewCardLimitNormal = 20;
    public const int NewCardLimitHeavy = 35;
    public const double HeavyEarlyShare = 0.35;
    public static readonly TimeSpan HeavyEarlyHorizon = TimeSpan.FromDays(4);

    private const double FallbackSecNew = 8.0;
    private const double FallbackSecReview = 4.0;
    private const double PriorTimingSamples = 20;
    private static readonly TimeSpan OverdueThreshold = TimeSpan.FromDays(1);

    public sealed record Candidate(CardRecord Card, int? FrequencyRank, double RecallProbability);

    public sealed record Plan(Intensity Intensity, int ReviewCount, int EarlyReviewCount, int NewCount, double EstimatedMinutes)
    {
        public IReadOnlyList<Candidate> DueCards { get; init; } = [];
        public IReadOnlyList<Candidate> EarlyReviewCards { get; init; } = [];
        public IReadOnlyList<Candidate> NewCards { get; init; } = [];
    }

    public sealed record Baselines(int DailyBaseReviews, double AvgSecNew, double AvgSecReview)
    {
        public double AvgSecLearning { get; init; } = FallbackSecReview;
        public double AvgSecOverdue { get; init; } = FallbackSecReview * 1.25;
        public double AvgSecEarly { get; init; } = FallbackSecReview * 1.1;

        public static Baselines Default { get; } = new(DefaultBaselineReviews, FallbackSecNew, FallbackSecReview);
    }

    public static Plan Build(Intensity intensity, IReadOnlyList<Candidate> candidates, Baselines baseline, DateTime nowUtc)
    {
        var now = AsUtc(nowUtc);
        var baseReviews = Math.Max(1, baseline.DailyBaseReviews);
        var baseReviewSeconds = Seconds(baseline.AvgSecReview, FallbackSecReview);
        var baselineSeconds = baseReviews * baseReviewSeconds;
        var factor = intensity switch
        {
            Intensity.Light => LightFactor,
            Intensity.Heavy => HeavyFactor,
            _ => NormalFactor,
        };
        var timeBudget = baselineSeconds * factor;

        var unique = candidates.DistinctBy(c => c.Card.Id).ToArray();
        var due = unique.Where(c => GetKind(c.Card, now) is WorkKind.Learning or WorkKind.Due or WorkKind.Overdue);
        var selectedDue = Select(due, baseline, now, timeBudget);
        var spentSeconds = TotalSeconds(selectedDue, baseline, now);
        var remainingSeconds = Math.Max(0, timeBudget - spentSeconds);

        IReadOnlyList<Candidate> selectedEarly = [];
        if (intensity == Intensity.Heavy)
        {
            var earlyBudget = Math.Min(remainingSeconds, baselineSeconds * HeavyEarlyShare);
            var early = unique.Where(c => GetKind(c.Card, now) == WorkKind.Early);
            selectedEarly = Select(early, baseline, now, earlyBudget);
            spentSeconds += TotalSeconds(selectedEarly, baseline, now);
            remainingSeconds = Math.Max(0, timeBudget - spentSeconds);
        }

        var newCardLimit = intensity switch
        {
            Intensity.Light => NewCardLimitLight,
            Intensity.Heavy => NewCardLimitHeavy,
            _ => NewCardLimitNormal,
        };
        var fresh = unique.Where(c => GetKind(c.Card, now) == WorkKind.New);
        var selectedNew = Select(fresh, baseline, now, remainingSeconds, newCardLimit);
        spentSeconds += TotalSeconds(selectedNew, baseline, now);

        return new Plan(intensity, selectedDue.Count, selectedEarly.Count, selectedNew.Count,
            Math.Round(spentSeconds / 60.0, 1))
        {
            DueCards = selectedDue,
            EarlyReviewCards = selectedEarly,
            NewCards = selectedNew,
        };
    }

    public static async Task<Baselines> LoadBaselinesAsync(AppDatabase db)
    {
        int dailyBase = await db.GetSettingIntAsync("baseline_daily_reviews", DefaultBaselineReviews).ConfigureAwait(false);
        var samples = await db.GetPlannerTimingStatsAsync().ConfigureAwait(false);

        var overallReview = Combine(samples.DueReview, samples.OverdueReview, samples.EarlyReview);
        var reviewPrior = SmoothedSeconds(overallReview, FallbackSecReview);
        return new Baselines(
            Math.Max(1, dailyBase),
            SmoothedSeconds(samples.NewCards, FallbackSecNew),
            SmoothedSeconds(samples.DueReview, reviewPrior))
        {
            AvgSecLearning = SmoothedSeconds(samples.LearningSteps, reviewPrior),
            AvgSecOverdue = SmoothedSeconds(samples.OverdueReview, reviewPrior * 1.25),
            AvgSecEarly = SmoothedSeconds(samples.EarlyReview, reviewPrior * 1.1),
        };
    }

    private static IReadOnlyList<Candidate> Select(
        IEnumerable<Candidate> candidates,
        Baselines baseline,
        DateTime nowUtc,
        double budgetSeconds,
        int maxCount = int.MaxValue)
    {
        var selected = new List<Candidate>();
        double remaining = Math.Max(0, budgetSeconds);
        foreach (var candidate in candidates
                     .Select(c => (Candidate: c, Seconds: EstimateSeconds(c, baseline, nowUtc)))
                     .OrderByDescending(item => Priority(item.Candidate, item.Seconds, nowUtc))
                     .ThenBy(item => item.Candidate.Card.DueUtc)
                     .ThenBy(item => item.Candidate.Card.Id))
        {
            if (selected.Count >= maxCount)
                break;
            if (candidate.Seconds > remaining)
                continue;
            selected.Add(candidate.Candidate);
            remaining -= candidate.Seconds;
        }
        return selected;
    }

    private static double TotalSeconds(IEnumerable<Candidate> candidates, Baselines baseline, DateTime nowUtc)
        => candidates.Sum(c => EstimateSeconds(c, baseline, nowUtc));

    private static double EstimateSeconds(Candidate candidate, Baselines baseline, DateTime nowUtc)
    {
        var kind = GetKind(candidate.Card, nowUtc);
        var baseSeconds = kind switch
        {
            WorkKind.New => Seconds(baseline.AvgSecNew, FallbackSecNew),
            WorkKind.Learning => Seconds(baseline.AvgSecLearning, Seconds(baseline.AvgSecReview, FallbackSecReview)),
            WorkKind.Overdue => Seconds(baseline.AvgSecOverdue, Seconds(baseline.AvgSecReview, FallbackSecReview) * 1.25),
            WorkKind.Early => Seconds(baseline.AvgSecEarly, Seconds(baseline.AvgSecReview, FallbackSecReview) * 1.1),
            _ => Seconds(baseline.AvgSecReview, FallbackSecReview),
        };

        if (kind == WorkKind.New)
            return baseSeconds;

        var difficulty = Math.Clamp(candidate.Card.Difficulty ?? 5, 1, 10);
        var difficultyFactor = 0.8 + difficulty / 25.0;
        var lapseFactor = 1 + Math.Min(Math.Max(0, candidate.Card.Lapses), 5) * 0.05;
        return baseSeconds * Math.Clamp(difficultyFactor * lapseFactor, 0.75, 1.6);
    }

    private static double Priority(Candidate candidate, double estimatedSeconds, DateTime nowUtc)
    {
        var kind = GetKind(candidate.Card, nowUtc);
        var importance = Importance(candidate.FrequencyRank);
        if (kind == WorkKind.New)
            return importance / estimatedSeconds;

        var recall = double.IsFinite(candidate.RecallProbability)
            ? Math.Clamp(candidate.RecallProbability, 0, 1)
            : 0;
        var risk = 0.2 + 0.8 * (1 - recall);
        var difficulty = Math.Clamp(candidate.Card.Difficulty ?? 5, 1, 10);
        var difficultHistory = 1 + Math.Max(0, difficulty - 5) * 0.06 + Math.Min(Math.Max(0, candidate.Card.Lapses), 5) * 0.15;
        var lateDays = Math.Max(0, (nowUtc - AsUtc(candidate.Card.DueUtc)).TotalDays);
        var urgency = 1 + Math.Min(0.8, lateDays / 7 * 0.8);
        return risk * importance * difficultHistory * urgency / estimatedSeconds;
    }

    private static double Importance(int? frequencyRank)
    {
        if (frequencyRank is not > 0)
            return 1;
        return 1 + 0.4 / (1 + Math.Log10(frequencyRank.Value));
    }

    private static WorkKind GetKind(CardRecord card, DateTime nowUtc)
    {
        var due = AsUtc(card.DueUtc);
        if (card.State == CardState.Learning && card.Reps == 0)
            return WorkKind.New;
        if ((card.State is CardState.Learning or CardState.Relearning) && card.Reps > 0 && due <= nowUtc)
            return WorkKind.Learning;
        if (card.State == CardState.Review && due <= nowUtc)
            return nowUtc - due >= OverdueThreshold ? WorkKind.Overdue : WorkKind.Due;
        if (card.State == CardState.Review && due <= nowUtc + HeavyEarlyHorizon)
            return WorkKind.Early;
        return WorkKind.Unavailable;
    }

    private static double SmoothedSeconds(TimingSamples samples, double prior)
    {
        var safePrior = Seconds(prior, FallbackSecReview);
        if (samples.Count <= 0 || !double.IsFinite(samples.TotalSeconds) || samples.TotalSeconds < 0)
            return safePrior;
        return Seconds((samples.TotalSeconds + safePrior * PriorTimingSamples) / (samples.Count + PriorTimingSamples), safePrior);
    }

    private static TimingSamples Combine(params TimingSamples[] samples)
        => new(samples.Sum(s => s.Count), samples.Sum(s => s.TotalSeconds));

    private static double Seconds(double value, double fallback)
        => double.IsFinite(value) && value > 0 ? Math.Clamp(value, 0.3, 120) : Math.Clamp(fallback, 0.3, 120);

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private enum WorkKind
    {
        Unavailable,
        New,
        Learning,
        Due,
        Overdue,
        Early,
    }
}
