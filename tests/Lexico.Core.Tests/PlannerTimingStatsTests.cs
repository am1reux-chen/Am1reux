using Lexico.Core.Data;
using Lexico.Core.Domain;
using Xunit;

namespace Lexico.Core.Tests;

public sealed class PlannerTimingStatsTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lexio-planner-time-" + Guid.NewGuid().ToString("N"));
    private readonly AppDatabase _db;

    public PlannerTimingStatsTests()
    {
        _db = new AppDatabase(Path.Combine(_dir, "planner.db"));
        _db.MigrateAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task TimingStatsSeparateNewLearningDueOverdueAndEarlyFromValidHistory()
    {
        var deckId = await _db.EnsureDeckAsync("timing", null, "test", builtIn: false);
        var wordId = await _db.UpsertWordAsync(new(0, "timing", null, null, 1, null, []));
        await _db.AddWordToDeckAsync(deckId, wordId, 0);
        await _db.EnsureCardsForDeckAsync(deckId);
        var card = (await _db.GetNewCardsAsync(1, deckId)).Single();

        var newAt = DateTime.UtcNow.AddDays(1);
        var learningAt = newAt.AddMinutes(1);
        var dueAt = learningAt.AddDays(1);
        var nextDue = dueAt.AddDays(1);
        var overdueAt = nextDue.AddDays(3);
        var earlyAt = overdueAt.AddDays(2);

        card = await SaveReviewAsync(card, newAt, CardState.Learning, repsBefore: 0, elapsedMs: 30_000, nextDue: learningAt);
        card = await SaveReviewAsync(card, learningAt, CardState.Learning, repsBefore: 1, elapsedMs: 6_000, nextDue: dueAt);
        card = await SaveReviewAsync(card, dueAt, CardState.Review, repsBefore: 2, elapsedMs: 7_000, nextDue: nextDue);
        card = await SaveReviewAsync(card, overdueAt, CardState.Review, repsBefore: 3, elapsedMs: 11_000, nextDue: overdueAt.AddDays(10));
        card = await SaveReviewAsync(card, earlyAt, CardState.Review, repsBefore: 4, elapsedMs: 13_000, nextDue: earlyAt.AddDays(10));
        _ = await SaveReviewAsync(card, earlyAt.AddDays(1), CardState.Review, repsBefore: 5, elapsedMs: 300_000,
            nextDue: earlyAt.AddDays(11), timingValid: false);

        var stats = await _db.GetPlannerTimingStatsAsync();

        Assert.Equal(new TimingSamples(1, 30), stats.NewCards);
        Assert.Equal(new TimingSamples(1, 6), stats.LearningSteps);
        Assert.Equal(new TimingSamples(1, 7), stats.DueReview);
        Assert.Equal(new TimingSamples(1, 11), stats.OverdueReview);
        Assert.Equal(new TimingSamples(1, 13), stats.EarlyReview);
    }

    [Fact]
    public async Task LoadedTimingBaselinesMoveGraduallyTowardObservedSpeed()
    {
        var deckId = await _db.EnsureDeckAsync("timing", null, "test", builtIn: false);
        var wordId = await _db.UpsertWordAsync(new(0, "timing", null, null, 1, null, []));
        await _db.AddWordToDeckAsync(deckId, wordId, 0);
        await _db.EnsureCardsForDeckAsync(deckId);
        var card = (await _db.GetNewCardsAsync(1, deckId)).Single();
        await SaveReviewAsync(card, DateTime.UtcNow.AddDays(1), CardState.Learning, repsBefore: 0,
            elapsedMs: 30_000, nextDue: DateTime.UtcNow.AddDays(2));

        var baseline = await Lexico.Core.Study.DailyPlanner.LoadBaselinesAsync(_db);

        Assert.InRange(baseline.AvgSecNew, 8.1, 29.9);
    }

    [Fact]
    public async Task PlanningCandidatesIncludeDueAndEarlyReviewsWithWordImportance()
    {
        var deckId = await _db.EnsureDeckAsync("planning", null, "test", builtIn: false);
        var ranks = new[] { 1, 100, 10_000 };
        for (int i = 0; i < ranks.Length; i++)
        {
            var wordId = await _db.UpsertWordAsync(new(0, $"word{i}", null, null, ranks[i], null, []));
            await _db.AddWordToDeckAsync(deckId, wordId, i);
        }
        await _db.EnsureCardsForDeckAsync(deckId);
        var newCards = await _db.GetNewCardsAsync(3, deckId);
        var otherDeckId = await _db.EnsureDeckAsync("other", null, "test", builtIn: false);
        var otherWordId = await _db.UpsertWordAsync(new(0, "other-word", null, null, 50, null, []));
        await _db.AddWordToDeckAsync(otherDeckId, otherWordId, 0);
        await _db.EnsureCardsForDeckAsync(otherDeckId);
        var otherCard = (await _db.GetNewCardsAsync(1, otherDeckId)).Single();
        var now = DateTime.UtcNow;
        var dueDates = new[] { now.AddDays(-2), now.AddHours(-1), now.AddDays(2) };
        for (int i = 0; i < newCards.Count; i++)
            await SaveReviewAsync(newCards[i], now.AddDays(-10), CardState.Learning, repsBefore: 0,
                elapsedMs: 1_000, nextDue: dueDates[i]);
        await SaveReviewAsync(otherCard, now.AddDays(-10), CardState.Learning, repsBefore: 0,
            elapsedMs: 1_000, nextDue: now.AddDays(-2));

        var candidates = await _db.GetPlanningCardsAsync(now, now.AddDays(4), deckId);

        Assert.Equal(3, candidates.Count);
        Assert.DoesNotContain(candidates, candidate => candidate.Card.DeckId == otherDeckId);
        Assert.Equal(new[] { 1, 100, 10_000 }, candidates.Select(candidate => candidate.FrequencyRank!.Value).Order().ToArray());
        Assert.Contains(candidates, candidate => candidate.Card.DueUtc < now.AddDays(-1));
        Assert.Contains(candidates, candidate => candidate.Card.DueUtc > now);
    }

    private async Task<CardRecord> SaveReviewAsync(
        CardRecord card,
        DateTime reviewedAt,
        CardState stateBefore,
        int repsBefore,
        double elapsedMs,
        DateTime nextDue,
        bool timingValid = true)
    {
        var updated = card with
        {
            State = CardState.Review,
            Step = null,
            Stability = 5,
            Difficulty = 5,
            DueUtc = nextDue,
            LastReviewUtc = reviewedAt,
            Reps = repsBefore + 1,
        };
        var log = new ReviewLogRow(
            0, card.Id, reviewedAt, Grade.Good, stateBefore, null, card.Stability, card.Difficulty,
            0, repsBefore, elapsedMs, elapsedMs, timingValid, 0.9, "test", nextDue,
            Math.Max(0, (nextDue - reviewedAt).TotalDays));
        await _db.AnswerCardAsync(updated, log);
        return updated;
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
