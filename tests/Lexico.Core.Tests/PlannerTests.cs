using Lexico.Core.Data;
using Lexico.Core.Domain;
using Lexico.Core.Study;
using Xunit;

namespace Lexico.Core.Tests;

public class PlannerTests
{
    private static DueSnapshot Due(int learning = 0, int review = 0, int fresh = 0, int within4 = 0)
        => new(learning, review, fresh, within4);

    [Fact]
    public void Light_Tier_HasNoNewWords_AndScalesDown()
    {
        var baseline = DailyPlanner.Baselines.Default; // 60 reviews
        var plan = DailyPlanner.Build(Intensity.Light, Due(review: 100, fresh: 50), baseline);

        Assert.Equal(0, plan.NewCount);
        Assert.True(plan.ReviewCount <= 33 + 1, $"light budget too big: {plan.ReviewCount}");
        Assert.True(plan.ReviewCount >= 30, $"light budget too small: {plan.ReviewCount}");
    }

    [Fact]
    public void Normal_Tier_MatchesBaseline_AndAddsNewWords()
    {
        // Capacity is expressed in review-equivalents: a new card costs 2x a review
        // (consistent with the 8s vs 4s fallback timing model).
        var plan = DailyPlanner.Build(Intensity.Normal, Due(review: 100, fresh: 50), DailyPlanner.Baselines.Default);

        Assert.InRange(plan.ReviewCount, 30, 40);
        Assert.InRange(plan.NewCount, 8, 12);
    }

    [Fact]
    public void Heavy_Tier_ExceedsNormal_AndAddsEarlyReviews()
    {
        var normal = DailyPlanner.Build(Intensity.Normal, Due(review: 20, fresh: 50, within4: 200), DailyPlanner.Baselines.Default);
        var heavy = DailyPlanner.Build(Intensity.Heavy, Due(review: 20, fresh: 50, within4: 200), DailyPlanner.Baselines.Default);

        Assert.True(heavy.ReviewCount + heavy.EarlyReviewCount > normal.ReviewCount);
        Assert.True(heavy.NewCount > normal.NewCount);
        Assert.True(heavy.EarlyReviewCount > 0, "heavy tier should schedule early reviews when capacity remains");
    }

    [Fact]
    public void Heavy_Tier_Backlog_FillsCapacityWithDueReviews_BeforeEarlyOnes()
    {
        var plan = DailyPlanner.Build(Intensity.Heavy, Due(review: 100, within4: 5000), DailyPlanner.Baselines.Default);

        Assert.Equal(0, plan.EarlyReviewCount); // due reviews take priority over early reviews
        Assert.True(plan.ReviewCount >= 55);
    }

    [Fact]
    public void EstimatedMinutes_UsePersonalSpeed_WhenAvailable()
    {
        var fast = new DailyPlanner.Baselines(60, AvgSecNew: 4, AvgSecReview: 2);
        var slow = new DailyPlanner.Baselines(60, AvgSecNew: 16, AvgSecReview: 8);
        var due = Due(review: 40, fresh: 100);

        var fastPlan = DailyPlanner.Build(Intensity.Normal, due, fast);
        var slowPlan = DailyPlanner.Build(Intensity.Normal, due, slow);

        Assert.True(fastPlan.EstimatedMinutes < slowPlan.EstimatedMinutes);
        Assert.True(slowPlan.EstimatedMinutes > 0);
    }

    [Fact]
    public void Backlog_DoesNotExplode_PlanIsCapped()
    {
        var plan = DailyPlanner.Build(Intensity.Heavy, Due(review: 5000, within4: 5000), DailyPlanner.Baselines.Default);

        Assert.True(plan.ReviewCount + plan.EarlyReviewCount + plan.NewCount <= 130,
            $"heavy plan exceeded cap: {plan.ReviewCount}+{plan.EarlyReviewCount}+{plan.NewCount}");
    }

    [Fact]
    public void EmptyQueue_ProducesEmptyPlan()
    {
        var plan = DailyPlanner.Build(Intensity.Normal, Due(), DailyPlanner.Baselines.Default);
        Assert.Equal(0, plan.ReviewCount);
        Assert.Equal(0, plan.NewCount);
        Assert.Equal(0, plan.EstimatedMinutes);
    }
}
