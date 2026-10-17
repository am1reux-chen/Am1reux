using Lexico.Core.Dictionary;
using Lexico.Core.Domain;
using Lexico.Core.Study;
using Xunit;

namespace Lexico.Core.Tests;

public sealed class StudyServiceTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lexio-study-" + Guid.NewGuid().ToString("N"));
    private readonly Data.AppDatabase _db;
    private readonly StudyService _study;

    public StudyServiceTests()
    {
        _db = new Data.AppDatabase(Path.Combine(_dir, "study.db"));
        _db.MigrateAsync().GetAwaiter().GetResult();
        new DictionaryService(_db).EnsureStarterDeckAsync().GetAwaiter().GetResult();
        _study = new StudyService(_db, new Scheduling.FsrsCoreEngine(enableFuzzing: false));
    }

    [Fact]
    public async Task Start_BuildsQueue_FromStarterDeck()
    {
        var plan = await _study.StartAsync(Intensity.Normal);

        Assert.True(plan.TotalCards > 0, "starter deck must yield cards");
        Assert.True(plan.NewCount > 0);
        var next = _study.PeekNext();
        Assert.NotNull(next);
        Assert.False(string.IsNullOrEmpty(next!.Lemma));
        Assert.NotNull(next.Senses as IEnumerable<WordSense>);
        await _study.FinishAsync();
    }

    [Fact]
    public async Task Answer_PersistsLog_AndAdvancesCard()
    {
        await _study.StartAsync(Intensity.Normal);
        var item = _study.PeekNext()!;
        long cardId = item.CardId;

        await _study.AnswerAsync(item, Grade.Good);

        // card advanced beyond "new"
        var card = await _db.GetCardAsync(cardId);
        Assert.NotNull(card);
        Assert.Equal(1, card!.Reps);
        Assert.NotNull(card.LastReviewUtc);

        var insights = await _db.GetInsightsAsync(DateTime.UtcNow);
        Assert.Equal(1, insights.ReviewsToday);
        Assert.Equal(1, insights.TotalReviews);
        await _study.FinishAsync();
    }

    [Fact]
    public async Task Answer_Again_RequeuesShortLearningStep_InSession()
    {
        await _study.StartAsync(Intensity.Normal);
        var item = _study.PeekNext()!;
        var requeuedAt = await _study.AnswerAsync(item, Grade.Again);

        Assert.NotNull(requeuedAt); // 1-minute step comes back inside the 30-minute window
        Assert.True(requeuedAt!.Value > DateTime.UtcNow.AddSeconds(-5));
        Assert.True(_study.Remaining > 0, "the requeued card must remain in the session queue");
    }

    [Fact]
    public async Task Finish_WritesSessionCounters()
    {
        await _study.StartAsync(Intensity.Normal);
        var first = _study.PeekNext()!;
        await _study.AnswerAsync(first, Grade.Good);
        var second = _study.PeekNext();
        if (second is not null)
            await _study.AnswerAsync(second, Grade.Again);

        var summary = await _study.FinishAsync();

        Assert.Equal(2, summary.Reviews);
        Assert.Equal(1, summary.Good);
        Assert.Equal(1, summary.Again);
        Assert.True(summary.Duration >= TimeSpan.Zero);
        Assert.False(_study.IsActive);
    }

    [Fact]
    public async Task Timing_Invalid_WhenWindowUnfocusedTooLong()
    {
        var t = new Scheduling.EffectiveTiming();
        t.AddUnfocused(30_000);
        await Task.Delay(50);
        var result = t.Finish();

        Assert.False(result.Valid);
        Assert.True(result.UnfocusedMs >= 30_000);
        Assert.True(result.RawMs >= 50);
    }

    [Fact]
    public async Task Timing_Valid_ForFocusedNormalAnswer()
    {
        var t = new Scheduling.EffectiveTiming();
        t.AddUnfocused(100);
        await Task.Delay(400);
        var result = t.Finish();

        Assert.True(result.Valid);
        Assert.True(result.EffectiveMs >= 300);
    }

    public async ValueTask DisposeAsync()
    {
        await _study.FinishAsync();
        await _db.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
