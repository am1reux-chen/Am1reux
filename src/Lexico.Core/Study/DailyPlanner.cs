using Lexico.Core.Data;
using Lexico.Core.Domain;

namespace Lexico.Core.Study;

/// <summary>Daily workload policy. Ratios are explicit constants — initial policy, tunable with real data.</summary>
public static class DailyPlanner
{
    public const double LightFactor = 0.55;
    public const double NormalFactor = 1.0;
    public const double HeavyFactor = 1.7;
    public const int DefaultBaselineReviews = 60;
    public const double NewShareNormal = 0.20;
    public const double NewShareHeavy = 0.35;
    /// <summary>Early-review horizon for the heavy tier.</summary>
    public static readonly TimeSpan HeavyEarlyHorizon = TimeSpan.FromDays(4);
    private const double FallbackSecNew = 8.0;
    private const double FallbackSecReview = 4.0;
    private const double NewCardCostVsReview = 2.0;

    public sealed record Plan(Intensity Intensity, int ReviewCount, int EarlyReviewCount, int NewCount, double EstimatedMinutes);

    public sealed record Baselines(int DailyBaseReviews, double AvgSecNew, double AvgSecReview)
    {
        public static Baselines Default { get; } = new(DefaultBaselineReviews, FallbackSecNew, FallbackSecReview);
    }

    public static Plan Build(Intensity intensity, DueSnapshot due, Baselines baseline)
    {
        double factor = intensity switch
        {
            Intensity.Light => LightFactor,
            Intensity.Normal => NormalFactor,
            Intensity.Heavy => HeavyFactor,
            _ => NormalFactor,
        };
        int capacity = Math.Max(5, (int)Math.Round(baseline.DailyBaseReviews * factor));
        int dueTotal = due.DueLearning + due.DueReview;

        double secNew = baseline.AvgSecNew > 0 ? baseline.AvgSecNew : FallbackSecNew;
        double secReview = baseline.AvgSecReview > 0 ? baseline.AvgSecReview : FallbackSecReview;

        int newTarget = intensity switch
        {
            Intensity.Light => 0,
            Intensity.Normal => (int)Math.Round(baseline.DailyBaseReviews * NewShareNormal),
            Intensity.Heavy => (int)Math.Round(baseline.DailyBaseReviews * NewShareHeavy),
            _ => 0,
        };

        int reviewBudget = Math.Max(0, capacity - (int)Math.Ceiling(newTarget * NewCardCostVsReview));
        int reviews = Math.Min(dueTotal, reviewBudget);
        int early = 0;
        if (intensity == Intensity.Heavy)
        {
            // Fill leftover review budget with low-horizon early reviews.
            int candidates = Math.Max(0, due.DueReviewWithin4Days - due.DueReview);
            early = Math.Min(candidates, Math.Max(0, reviewBudget - reviews));
        }

        // Debt control: when overdue exceeds the budget we never reset due dates —
        // the surplus simply stays due for the following days (visible in Insights).
        int remainingCapacity = Math.Max(0, capacity - reviews - early);
        int newCount = intensity == Intensity.Light
            ? 0
            : Math.Min(Math.Min(due.NewAvailable, newTarget), (int)(remainingCapacity / NewCardCostVsReview));

        double minutes = (reviews + early) * secReview / 60.0 + newCount * secNew / 60.0;
        return new Plan(intensity, reviews, early, newCount, Math.Round(minutes, 1));
    }

    public static async Task<Baselines> LoadBaselinesAsync(Data.AppDatabase db)
    {
        int baseline = await db.GetSettingIntAsync("baseline_daily_reviews", DefaultBaselineReviews).ConfigureAwait(false);
        double avgNew = await db.GetAvgSecondsAsync(newCards: true).ConfigureAwait(false);
        double avgReview = await db.GetAvgSecondsAsync(newCards: false).ConfigureAwait(false);
        return new Baselines(baseline, avgNew, avgReview);
    }
}
