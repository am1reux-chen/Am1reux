using Lexico.Core.Domain;
using Lexico.Core.Study;
using Xunit;

namespace Lexico.Core.Tests;

public class PlannerTests
{
    private static readonly DateTime Now = new(2026, 10, 17, 0, 0, 0, DateTimeKind.Utc);

    private static DailyPlanner.Candidate DueReview(
        long id,
        double recall = 0.5,
        int? rank = null,
        double difficulty = 5,
        int lapses = 0,
        TimeSpan? overdue = null)
        => Candidate(id, CardState.Review, Now - (overdue ?? TimeSpan.Zero), reps: 4, recall, rank, difficulty, lapses);

    private static DailyPlanner.Candidate EarlyReview(long id, int? rank = null)
        => Candidate(id, CardState.Review, Now.AddDays(1), reps: 4, 0.5, rank, 5, 0);

    private static DailyPlanner.Candidate NewCard(long id, int? rank = null)
        => Candidate(id, CardState.Learning, Now, reps: 0, 0, rank, null, 0);

    private static DailyPlanner.Candidate Candidate(
        long id,
        CardState state,
        DateTime dueUtc,
        int reps,
        double recall,
        int? rank,
        double? difficulty,
        int lapses)
    {
        var card = new CardRecord(
            id, id, 1, state, null, state == CardState.Review ? 10 : null, difficulty,
            dueUtc, state == CardState.Learning && reps == 0 ? null : Now.AddDays(-2), reps, lapses, Now.AddDays(-10));
        return new DailyPlanner.Candidate(card, rank, recall);
    }

    [Fact]
    public void Light_SelectsHighValueDueCardsOnly_Within55PercentTimeBudget()
    {
        var candidates = Enumerable.Range(1, 10)
            .Select(id => DueReview(id, recall: id == 1 ? 0.1 : 0.9, rank: id == 1 ? 1 : 10_000,
                difficulty: id == 1 ? 9 : 3, lapses: id == 1 ? 3 : 0))
            .Concat([NewCard(20), EarlyReview(21)])
            .ToArray();
        var baseline = new DailyPlanner.Baselines(10, AvgSecNew: 20, AvgSecReview: 10);

        var plan = DailyPlanner.Build(Intensity.Light, candidates, baseline, Now);

        Assert.Equal(5, plan.ReviewCount);
        Assert.Equal(0, plan.NewCount);
        Assert.Equal(0, plan.EarlyReviewCount);
        Assert.Contains(plan.DueCards, card => card.Card.Id == 1);
        Assert.DoesNotContain(plan.DueCards, card => card.Card.Id == 10);
        Assert.True(plan.EstimatedMinutes <= 55d / 60d);
    }

    [Fact]
    public void Light_PrefersHigherRiskWhenOtherPrioritySignalsAndCostMatch()
    {
        var candidates = Enumerable.Range(1, 8)
            .Select(id => DueReview(id, recall: id <= 4 ? 0.2 : 0.9, rank: 100, difficulty: 5))
            .ToArray();
        var baseline = new DailyPlanner.Baselines(10, AvgSecNew: 20, AvgSecReview: 10);

        var plan = DailyPlanner.Build(Intensity.Light, candidates, baseline, Now);

        Assert.All(candidates.Take(4), highRisk => Assert.Contains(plan.DueCards, selected => selected.Card.Id == highRisk.Card.Id));
        Assert.DoesNotContain(plan.DueCards, selected => selected.Card.Id == 8);
    }

    [Fact]
    public void Light_PrefersImportantAndHistoricallyDifficultCardsPerSecond()
    {
        var candidates = Enumerable.Range(1, 8)
            .Select(id => DueReview(id, recall: 0.5, rank: id <= 4 ? 1 : 10_000,
                difficulty: id == 1 ? 10 : 5, lapses: id == 1 ? 5 : 0))
            .ToArray();
        var baseline = new DailyPlanner.Baselines(10, AvgSecNew: 20, AvgSecReview: 10);

        var plan = DailyPlanner.Build(Intensity.Light, candidates, baseline, Now);

        Assert.Contains(plan.DueCards, selected => selected.Card.Id == 1);
        Assert.All(candidates.Where(candidate => candidate.FrequencyRank == 1), important =>
            Assert.Contains(plan.DueCards, selected => selected.Card.Id == important.Card.Id));
        Assert.DoesNotContain(plan.DueCards, selected => selected.Card.Id == 8);
    }

    [Fact]
    public void Normal_CompletesMainDueReviewsBeforeUsingRemainingTimeForNewCards()
    {
        var candidates = Enumerable.Range(1, 5).Select(id => DueReview(id))
            .Concat(Enumerable.Range(20, 20).Select(id => NewCard(id)))
            .Concat([EarlyReview(50)])
            .ToArray();
        var baseline = new DailyPlanner.Baselines(10, AvgSecNew: 20, AvgSecReview: 10);

        var plan = DailyPlanner.Build(Intensity.Normal, candidates, baseline, Now);

        Assert.Equal(5, plan.ReviewCount);
        Assert.Equal(2, plan.NewCount); // new cards use remaining time up to the normal budget
        Assert.Equal(0, plan.EarlyReviewCount);
        Assert.Equal(1.5, plan.EstimatedMinutes);
    }

    [Fact]
    public void Heavy_AddsEarlyReviewsAndNewCardsOnlyAfterDueReviews()
    {
        var candidates = Enumerable.Range(1, 2).Select(id => DueReview(id))
            .Concat(Enumerable.Range(20, 10).Select(id => EarlyReview(id)))
            .Concat(Enumerable.Range(40, 10).Select(id => NewCard(id)))
            .ToArray();
        var baseline = new DailyPlanner.Baselines(10, AvgSecNew: 20, AvgSecReview: 10)
        {
            AvgSecEarly = 10,
        };

        var plan = DailyPlanner.Build(Intensity.Heavy, candidates, baseline, Now);

        Assert.Equal(2, plan.ReviewCount);
        Assert.Equal(3, plan.EarlyReviewCount); // at most 35 seconds of the 100-second baseline
        Assert.Equal(6, plan.NewCount); // then use the remaining heavy budget for new cards
        Assert.True(plan.EstimatedMinutes <= 170d / 60d);
    }

    [Fact]
    public void ReviewCountsFollowThe55_100_170PercentTimeBudgets()
    {
        var candidates = Enumerable.Range(1, 500).Select(id => DueReview(id)).ToArray();
        var baseline = new DailyPlanner.Baselines(60, AvgSecNew: 8, AvgSecReview: 4);

        var light = DailyPlanner.Build(Intensity.Light, candidates, baseline, Now);
        var normal = DailyPlanner.Build(Intensity.Normal, candidates, baseline, Now);
        var heavy = DailyPlanner.Build(Intensity.Heavy, candidates, baseline, Now);

        Assert.Equal(33, light.ReviewCount);
        Assert.Equal(60, normal.ReviewCount);
        Assert.Equal(102, heavy.ReviewCount);
        Assert.InRange(light.EstimatedMinutes, 2.1, 2.2);
        Assert.Equal(4.0, normal.EstimatedMinutes);
        Assert.Equal(6.8, heavy.EstimatedMinutes);
    }

    [Fact]
    public void NewWordPlansOfferMoreWordsAtEachIntensityWhenNoReviewsAreDue()
    {
        var candidates = Enumerable.Range(1, 100).Select(id => NewCard(id)).ToArray();
        var baseline = new DailyPlanner.Baselines(60, AvgSecNew: 12, AvgSecReview: 4);

        var light = DailyPlanner.Build(Intensity.Light, candidates, baseline, Now);
        var normal = DailyPlanner.Build(Intensity.Normal, candidates, baseline, Now);
        var heavy = DailyPlanner.Build(Intensity.Heavy, candidates, baseline, Now);

        Assert.Equal(10, light.NewCount);
        Assert.Equal(20, normal.NewCount);
        Assert.Equal(34, heavy.NewCount);
        Assert.Equal(2.0, light.EstimatedMinutes);
        Assert.Equal(4.0, normal.EstimatedMinutes);
        Assert.Equal(6.8, heavy.EstimatedMinutes);
    }

    [Fact]
    public void NewWordPlansRespectDailyTargetsWithoutTimingHistory()
    {
        var candidates = Enumerable.Range(1, 100).Select(id => NewCard(id)).ToArray();
        var baseline = new DailyPlanner.Baselines(60, AvgSecNew: 8, AvgSecReview: 4);

        var light = DailyPlanner.Build(Intensity.Light, candidates, baseline, Now);
        var normal = DailyPlanner.Build(Intensity.Normal, candidates, baseline, Now);
        var heavy = DailyPlanner.Build(Intensity.Heavy, candidates, baseline, Now);

        Assert.Equal(10, light.NewCount);
        Assert.Equal(20, normal.NewCount);
        Assert.Equal(35, heavy.NewCount);
    }

    [Fact]
    public void NewCardCountUsesEstimatedTimeInsteadOfFixedWordCount()
    {
        var candidates = Enumerable.Range(1, 100).Select(id => NewCard(id)).ToArray();
        var baseline = new DailyPlanner.Baselines(10, AvgSecNew: 5, AvgSecReview: 10);
        var slowNewCards = baseline with { AvgSecNew = 25 };

        var fastPlan = DailyPlanner.Build(Intensity.Normal, candidates, baseline, Now);
        var slowPlan = DailyPlanner.Build(Intensity.Normal, candidates, slowNewCards, Now);

        Assert.Equal(20, fastPlan.NewCount);
        Assert.Equal(4, slowPlan.NewCount);
        Assert.Equal(1.7, fastPlan.EstimatedMinutes);
        Assert.Equal(1.7, slowPlan.EstimatedMinutes);
    }

    [Fact]
    public void EstimatesDifferentCostsForNewLearningDueOverdueAndEarlyCards()
    {
        var baseline = new DailyPlanner.Baselines(10, AvgSecNew: 20, AvgSecReview: 10)
        {
            AvgSecLearning = 5,
            AvgSecOverdue = 15,
            AvgSecEarly = 8,
        };
        var candidates = new[]
        {
            DueReview(1, overdue: TimeSpan.FromDays(2)),
            DueReview(2, overdue: TimeSpan.FromHours(2)),
            Candidate(3, CardState.Learning, Now.AddMinutes(-1), 2, 0.4, null, 5, 0),
            EarlyReview(4),
            NewCard(5),
        };

        var plan = DailyPlanner.Build(Intensity.Heavy, candidates, baseline, Now);

        Assert.Equal(3, plan.ReviewCount);
        Assert.Equal(1, plan.EarlyReviewCount);
        Assert.Equal(1, plan.NewCount);
        Assert.Equal(58d / 60d, plan.EstimatedMinutes, precision: 1);
    }

    [Fact]
    public void Backlog_IsCappedByTimeBudget_AndDoesNotMoveDueDates()
    {
        var candidates = Enumerable.Range(1, 5000).Select(id => DueReview(id)).ToArray();
        var originalDue = candidates[0].Card.DueUtc;
        var baseline = new DailyPlanner.Baselines(10, AvgSecNew: 20, AvgSecReview: 10);

        var plan = DailyPlanner.Build(Intensity.Heavy, candidates, baseline, Now);

        Assert.Equal(17, plan.ReviewCount);
        Assert.Empty(plan.EarlyReviewCards);
        Assert.Empty(plan.NewCards);
        Assert.Equal(170d / 60d, plan.EstimatedMinutes, precision: 1);
        Assert.Equal(originalDue, candidates[0].Card.DueUtc);
    }

    [Fact]
    public void DuplicateCandidateIdsAreQueuedOnlyOnce()
    {
        var duplicate = DueReview(1);
        var plan = DailyPlanner.Build(Intensity.Normal, [duplicate, duplicate],
            new DailyPlanner.Baselines(10, AvgSecNew: 20, AvgSecReview: 10), Now);

        Assert.Single(plan.DueCards);
    }

    [Fact]
    public void UtcDayBoundaryAndInvalidBaselinesRemainFinite()
    {
        var boundary = new DateTime(2026, 10, 17, 23, 59, 0, DateTimeKind.Utc);
        var candidate = Candidate(1, CardState.Review, boundary, reps: 2, recall: 0.5, rank: null, difficulty: 5, lapses: 0);
        var invalidBaseline = new DailyPlanner.Baselines(-10, double.NaN, double.PositiveInfinity);

        var plan = DailyPlanner.Build(Intensity.Heavy, [candidate], invalidBaseline, boundary);

        Assert.Equal(1, plan.ReviewCount);
        Assert.True(double.IsFinite(plan.EstimatedMinutes));
    }

    [Fact]
    public void LocalAndUtcRepresentationsOfTheSameInstantProduceTheSamePlan()
    {
        var nowUtc = new DateTime(2026, 10, 17, 23, 30, 0, DateTimeKind.Utc);
        var candidate = Candidate(1, CardState.Review, nowUtc, reps: 2, recall: 0.5, rank: null, difficulty: 5, lapses: 0);
        var baseline = new DailyPlanner.Baselines(10, AvgSecNew: 20, AvgSecReview: 10);

        var utcPlan = DailyPlanner.Build(Intensity.Normal, [candidate], baseline, nowUtc);
        var localPlan = DailyPlanner.Build(Intensity.Normal, [candidate], baseline, nowUtc.ToLocalTime());

        Assert.Equal(utcPlan.ReviewCount, localPlan.ReviewCount);
        Assert.Equal(utcPlan.EarlyReviewCount, localPlan.EarlyReviewCount);
        Assert.Equal(utcPlan.NewCount, localPlan.NewCount);
        Assert.Equal(utcPlan.EstimatedMinutes, localPlan.EstimatedMinutes);
        Assert.Equal(utcPlan.DueCards.Select(card => card.Card.Id), localPlan.DueCards.Select(card => card.Card.Id));
    }

    [Fact]
    public void EmptyQueue_ProducesEmptyPlan()
    {
        var plan = DailyPlanner.Build(Intensity.Normal, [], DailyPlanner.Baselines.Default, Now);

        Assert.Equal(0, plan.ReviewCount);
        Assert.Equal(0, plan.NewCount);
        Assert.Equal(0, plan.EstimatedMinutes);
    }
}
