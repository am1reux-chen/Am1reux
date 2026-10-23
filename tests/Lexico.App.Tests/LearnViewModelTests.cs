using Lexio.Services;
using Lexio.ViewModels;
using CommunityToolkit.Mvvm.Input;
using Lexico.Core.Data;
using Lexico.Core.Dictionary;
using Lexico.Core.Domain;
using Lexico.Core.Scheduling;
using Lexico.Core.Study;
using Xunit;

namespace Lexico.App.Tests;

public sealed class LearnViewModelTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lexio-learn-viewmodel-" + Guid.NewGuid().ToString("N"));
    private AppDatabase _db = null!;
    private StudyService _study = null!;
    private AppServices _services = null!;

    public async Task InitializeAsync()
    {
        _db = new AppDatabase(Path.Combine(_directory, "study.db"));
        await _db.MigrateAsync();
        var dictionary = new DictionaryService(_db);
        await dictionary.EnsureStarterDeckAsync();
        _study = new StudyService(_db, new FsrsCoreEngine(enableFuzzing: false));
        _services = new AppServices(_study);
    }

    [Fact]
    public async Task RecreatedViewModelRestoresCommittedProgressAndCurrentCard()
    {
        var plan = await _study.StartAsync(Intensity.Normal);
        var firstPage = new LearnViewModel(_services);
        await firstPage.ResumeAsync();
        var firstCard = Assert.IsType<StudyItem>(firstPage.Current);

        await _study.AnswerAsync(firstCard, Grade.Good);
        var expectedCurrent = Assert.IsType<StudyItem>(_study.PeekNext());

        for (int entry = 0; entry < 3; entry++)
        {
            var returnedPage = new LearnViewModel(_services);
            await returnedPage.ResumeAsync();

            Assert.Equal($"0 / {plan.TotalCards}", returnedPage.ProgressText);
            Assert.Equal(0d, returnedPage.ProgressFraction, 10);
            Assert.Equal(1, returnedPage.CompletedCount);
            Assert.Equal(_study.Remaining, returnedPage.RemainingCount);
            Assert.Equal(expectedCurrent.CardId, returnedPage.Current!.CardId);
        }
    }

    [Fact]
    public async Task RecreatedViewModelKeepsAnUngradedCardRevealed()
    {
        await _study.StartAsync(Intensity.Normal);
        var firstPage = new LearnViewModel(_services);
        await firstPage.ResumeAsync();
        var cardId = firstPage.Current!.CardId;
        firstPage.RevealCommand.Execute(null);

        var returnedPage = new LearnViewModel(_services);
        await returnedPage.ResumeAsync();

        Assert.Equal(cardId, returnedPage.Current!.CardId);
        Assert.True(returnedPage.Revealed);
        Assert.Equal(0, returnedPage.CompletedCount);
        Assert.Equal(_study.Remaining, returnedPage.RemainingCount);
    }

    [Fact]
    public async Task RecoveredSessionRestoresProgressAndCardButNotTransientReveal()
    {
        var plan = await _study.StartAsync(Intensity.Normal);
        var firstPage = new LearnViewModel(_services);
        await firstPage.ResumeAsync();
        firstPage.RevealCommand.Execute(null);
        await firstPage.GradeCommand.ExecuteAsync("3");
        var current = firstPage.Current!;
        firstPage.RevealCommand.Execute(null);
        int remaining = _study.Remaining;

        await _db.DisposeAsync();
        _db = new AppDatabase(Path.Combine(_directory, "study.db"));
        await _db.MigrateAsync();
        _study = new StudyService(_db, new FsrsCoreEngine(enableFuzzing: false));
        Assert.Equal(1, await _study.RecoverInterruptedSessionsAsync());
        Assert.False(_study.IsPaused);
        Assert.Equal(1, _study.CompletedReviews);
        Assert.Equal(remaining, _study.Remaining);
        Assert.False(_study.IsCardRevealed(current.CardId));

        _services = new AppServices(_study);
        var returnedPage = new LearnViewModel(_services);
        await returnedPage.ResumeAsync();

        Assert.Equal($"0 / {plan.TotalCards}", returnedPage.ProgressText);
        Assert.Equal(current.CardId, returnedPage.Current!.CardId);
        Assert.False(returnedPage.Revealed);
        Assert.Equal(remaining, returnedPage.RemainingCount);
    }

    [Fact]
    public async Task RequeuedAgainDoesNotAdvancePlannedCardProgress()
    {
        var plan = await _study.StartAsync(Intensity.Normal);
        var viewModel = new LearnViewModel(_services) { FourGradeMode = true };
        await viewModel.ResumeAsync();

        viewModel.RevealCommand.Execute(null);
        await viewModel.GradeCommand.ExecuteAsync("1");

        Assert.Equal(1, viewModel.CompletedCount);
        Assert.Equal(plan.TotalCards, viewModel.RemainingCount);
        Assert.Equal($"0 / {plan.TotalCards}", viewModel.ProgressText);
        Assert.Equal(0, viewModel.ProgressFraction, 10);

        viewModel.RevealCommand.Execute(null);
        await viewModel.GradeCommand.ExecuteAsync("4");

        Assert.Equal(plan.TotalCards - 1, viewModel.RemainingCount);
        Assert.Equal($"1 / {plan.TotalCards}", viewModel.ProgressText);
        Assert.Equal(1d / plan.TotalCards, viewModel.ProgressFraction, 10);
    }

    [Fact]
    public async Task EndingBeforeQueueIsDrainedReportsThatTheSessionEndedEarly()
    {
        var plan = await _study.StartAsync(Intensity.Normal);
        var viewModel = new LearnViewModel(_services);
        await viewModel.ResumeAsync();

        await viewModel.EndCommand.ExecuteAsync(null);

        Assert.Equal("本次学习已结束", viewModel.SummaryText);
        Assert.Contains($"队列剩余 {plan.TotalCards} 张", viewModel.SummaryDetail);
        Assert.Equal($"0 / {plan.TotalCards}", viewModel.ProgressText);
        Assert.Equal(0, viewModel.ProgressFraction, 10);
    }

    [Fact]
    public async Task DrainingThePlannedQueueUsesThePlanCompleteSummary()
    {
        var plan = await _study.StartAsync(Intensity.Normal);
        var viewModel = new LearnViewModel(_services) { FourGradeMode = true };
        await viewModel.ResumeAsync();

        for (int i = 0; i < plan.TotalCards; i++)
        {
            viewModel.RevealCommand.Execute(null);
            await viewModel.GradeCommand.ExecuteAsync("4");
        }

        Assert.True(viewModel.Done);
        Assert.Equal("今日计划已完成", viewModel.SummaryText);
        Assert.Contains($"复习 {plan.TotalCards} 次", viewModel.SummaryDetail);
        Assert.DoesNotContain("队列剩余", viewModel.SummaryDetail);
    }

    [Fact]
    public void KeyboardHintMatchesGradeModeAndNotifiesWhenModeChanges()
    {
        var viewModel = new LearnViewModel(_services);
        int hintChanges = 0;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(viewModel.KeyboardHint))
                hintChanges++;
        };

        Assert.Contains("1/3 评分", viewModel.KeyboardHint);
        viewModel.FourGradeMode = true;
        Assert.Contains("1–4 评分", viewModel.KeyboardHint);
        viewModel.FourGradeMode = false;
        Assert.Contains("1/3 评分", viewModel.KeyboardHint);
        Assert.Equal(2, hintChanges);
    }

    [Fact]
    public async Task ShowQuestionNotifiesWhenRevealStateChanges()
    {
        await _study.StartAsync(Intensity.Normal);
        var viewModel = new LearnViewModel(_services);
        var changes = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        await viewModel.ResumeAsync();
        changes.Clear();

        viewModel.RevealCommand.Execute(null);

        Assert.False(viewModel.ShowQuestion);
        Assert.Contains(nameof(viewModel.ShowQuestion), changes);
    }

    [Fact]
    public async Task ShowNewBadgeNotifiesWhenCardBadgeChanges()
    {
        await _study.StartAsync(Intensity.Normal);
        var viewModel = new LearnViewModel(_services);
        var changes = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        await viewModel.ResumeAsync();

        Assert.Equal("NEW", viewModel.NewBadge);
        Assert.True(viewModel.ShowNewBadge);
        changes.Clear();
        viewModel.NewBadge = string.Empty;

        Assert.False(viewModel.ShowNewBadge);
        Assert.Contains(nameof(viewModel.ShowNewBadge), changes);
    }

    [Fact]
    public async Task CardVisibilityDerivedStatesNotifyWhenSessionHidesCard()
    {
        await _study.StartAsync(Intensity.Normal);
        var viewModel = new LearnViewModel(_services);
        await viewModel.ResumeAsync();
        var changes = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        await viewModel.PauseCommand.ExecuteAsync(null);

        Assert.False(viewModel.ShowCard);
        Assert.Contains(nameof(viewModel.ShowQuestion), changes);
        Assert.Contains(nameof(viewModel.ShowNewBadge), changes);
    }

    [Fact]
    public void NotFourGradeModeNotifiesWhenModeChanges()
    {
        var viewModel = new LearnViewModel(_services);
        var changes = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        viewModel.FourGradeMode = true;

        Assert.False(viewModel.NotFourGradeMode);
        Assert.Contains(nameof(viewModel.NotFourGradeMode), changes);
    }

    [Fact]
    public async Task SpeakCommandIsAvailableOnlyWhileAStudyCardIsVisible()
    {
        var viewModel = new LearnViewModel(_services);
        int availabilityChanges = 0;
        viewModel.SpeakCommand.CanExecuteChanged += (_, _) => availabilityChanges++;

        Assert.False(viewModel.SpeakCommand.CanExecute(null));
        await _study.StartAsync(Intensity.Normal);
        await viewModel.ResumeAsync();
        Assert.True(viewModel.SpeakCommand.CanExecute(null));

        await viewModel.PauseCommand.ExecuteAsync(null);
        Assert.False(viewModel.SpeakCommand.CanExecute(null));

        await viewModel.ResumeCommand.ExecuteAsync(null);
        Assert.True(viewModel.SpeakCommand.CanExecute(null));

        await viewModel.EndCommand.ExecuteAsync(null);
        Assert.False(viewModel.SpeakCommand.CanExecute(null));
        Assert.True(availabilityChanges >= 4);
    }

    [Fact]
    public async Task MissingMeaningCardCannotBeGradedEvenWhenGradeCommandIsInvokedDirectly()
    {
        var wordId = await _db.UpsertWordAsync(new(0, "untranslated", null, null, null, null,
            [new(0, "n.", null, null, null, null)]));
        var deckId = await _db.EnsureDeckAsync("No meaning", null, "test", builtIn: true);
        await _db.AddWordToDeckAsync(deckId, wordId, 0);
        await _db.EnsureCardsForDeckAsync(deckId);
        await _study.StartAsync(Intensity.Normal, deckId);

        var viewModel = new LearnViewModel(_services);
        await viewModel.ResumeAsync();
        long cardId = viewModel.Current!.CardId;
        viewModel.RevealCommand.Execute(null);

        Assert.True(viewModel.Revealed);
        Assert.Empty(viewModel.Senses);
        Assert.Contains("不能评分", viewModel.KeyboardHint);
        Assert.DoesNotContain("评分", viewModel.KeyboardHint.Replace("不能评分", "", StringComparison.Ordinal));
        Assert.False(viewModel.GradeCommand.CanExecute("3"));

        await viewModel.GradeCommand.ExecuteAsync("3");

        Assert.Equal(0, _study.CompletedReviews);
        Assert.Equal(0, (await _db.GetInsightsAsync(DateTime.UtcNow)).ReviewsToday);
        Assert.Equal(cardId, viewModel.Current!.CardId);
        Assert.True(viewModel.Revealed);
    }

    [Fact]
    public async Task RevealedCardExposesImportedRichWordDetails()
    {
        var details = new WordDetails(
            [new("The sample sentence.", "示例句。")],
            [new("sample phrase", "示例短语")],
            [new("n.", "样例", ["specimen"])],
            [new("sample", "From Latin exemplum.")],
            "exampl",
            [new("v.", [new("exemplify", "举例说明")])]);
        var wordId = await _db.UpsertWordAsync(new WordEntry(0, "sample", "/ˈsæmpəl/", null, null, null,
            [new(0, "n.", "样本", null, null, null)]) { Details = details });
        var deckId = await _db.EnsureDeckAsync("Rich details", null, "test", builtIn: false);
        await _db.AddWordToDeckAsync(deckId, wordId, 0);
        await _db.EnsureCardsForDeckAsync(deckId);
        await _study.StartAsync(Intensity.Normal, deckId);

        var viewModel = new LearnViewModel(new AppServices(_db, _study));
        await viewModel.ResumeAsync();
        viewModel.RevealCommand.Execute(null);

        Assert.Collection(viewModel.DetailSections,
            section =>
            {
                Assert.Equal("例句", section.Title);
                var line = Assert.Single(section.Lines);
                Assert.Equal("The sample sentence.", line.Primary);
                Assert.Equal("示例句。", line.Secondary);
            },
            section =>
            {
                Assert.Equal("短语", section.Title);
                var line = Assert.Single(section.Lines);
                Assert.Equal("sample phrase", line.Primary);
                Assert.Equal("示例短语", line.Secondary);
            },
            section =>
            {
                Assert.Equal("近义词", section.Title);
                var line = Assert.Single(section.Lines);
                Assert.Equal("n. 样例", line.Primary);
                Assert.Equal("specimen", line.Secondary);
            },
            section =>
            {
                Assert.Equal("词源", section.Title);
                var line = Assert.Single(section.Lines);
                Assert.Equal("sample", line.Primary);
                Assert.Equal("From Latin exemplum.", line.Secondary);
            },
            section =>
            {
                Assert.Equal("相关词", section.Title);
                Assert.Equal(2, section.Lines.Count);
                Assert.Equal("词根 · exampl", section.Lines[0].Primary);
                Assert.Equal("exemplify", section.Lines[1].Primary);
                Assert.Equal("v. 举例说明", section.Lines[1].Secondary);
            });
    }

    [Fact]
    public async Task PauseAndResumePreservesTheLiveCardWithoutEndingTheSession()
    {
        await _study.StartAsync(Intensity.Normal);
        var viewModel = new LearnViewModel(_services);
        await viewModel.ResumeAsync();
        long cardId = viewModel.Current!.CardId;
        int remaining = _study.Remaining;
        viewModel.RevealCommand.Execute(null);

        var pauseProperty = typeof(LearnViewModel).GetProperty("PauseCommand");
        Assert.NotNull(pauseProperty);
        var pauseCommand = Assert.IsAssignableFrom<IAsyncRelayCommand>(pauseProperty.GetValue(viewModel));
        await pauseCommand.ExecuteAsync(null);

        var pausedProperty = typeof(LearnViewModel).GetProperty("IsPaused");
        Assert.NotNull(pausedProperty);
        Assert.True((bool)pausedProperty.GetValue(viewModel)!);
        Assert.True(viewModel.ShowPaused);
        Assert.True(_study.IsActive);
        Assert.Equal(remaining, _study.Remaining);
        Assert.Equal(cardId, viewModel.Current!.CardId);
        Assert.Equal(0, (await _db.GetInsightsAsync(DateTime.UtcNow)).ReviewsToday);
        Assert.Null(await _study.AnswerAsync(viewModel.Current!, Grade.Good));

        var resumeProperty = typeof(LearnViewModel).GetProperty("ResumeCommand");
        Assert.NotNull(resumeProperty);
        var resumeCommand = Assert.IsAssignableFrom<IAsyncRelayCommand>(resumeProperty.GetValue(viewModel));
        await resumeCommand.ExecuteAsync(null);

        Assert.True(viewModel.InSession);
        Assert.False(viewModel.ShowPaused);
        Assert.False((bool)pausedProperty.GetValue(viewModel)!);
        Assert.Equal(cardId, viewModel.Current!.CardId);
        Assert.True(viewModel.Revealed);
        Assert.Equal(remaining, _study.Remaining);
        Assert.Equal(0, viewModel.CompletedCount);
        Assert.Equal(remaining, viewModel.RemainingCount);

        await viewModel.GradeCommand.ExecuteAsync("3");
        Assert.Equal(1, viewModel.CompletedCount);
        Assert.Equal(1, (await _db.GetInsightsAsync(DateTime.UtcNow)).ReviewsToday);
    }

    [Fact]
    public async Task HeaderPauseCommandRemainsAvailableToResumeAPausedSession()
    {
        await _study.StartAsync(Intensity.Normal);
        var viewModel = new LearnViewModel(_services);
        await viewModel.ResumeAsync();
        var pauseCommand = viewModel.PauseCommand;

        Assert.Equal("暂停", viewModel.PauseButtonAccessibleName);
        Assert.True(pauseCommand.CanExecute(null));
        await pauseCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsPaused);
        Assert.Equal("继续学习", viewModel.PauseButtonAccessibleName);
        Assert.True(pauseCommand.CanExecute(null));

        await pauseCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsPaused);
        Assert.Equal("暂停", viewModel.PauseButtonAccessibleName);
        Assert.True(viewModel.InSession);
        Assert.True(_study.IsActive);
        await _study.FinishAsync();
    }

    [Fact]
    public async Task ConsecutiveAnswersPersistOnceAndReentryDoesNotRepeatThem()
    {
        var plan = await _study.StartAsync(Intensity.Normal);
        var viewModel = new LearnViewModel(_services);
        await viewModel.ResumeAsync();
        var firstCard = viewModel.Current!;

        viewModel.RevealCommand.Execute(null);
        await viewModel.GradeCommand.ExecuteAsync("3");
        var secondCard = viewModel.Current!;
        Assert.NotEqual(firstCard.CardId, secondCard.CardId);

        viewModel.RevealCommand.Execute(null);
        await viewModel.GradeCommand.ExecuteAsync("3");
        Assert.Equal($"0 / {plan.TotalCards}", viewModel.ProgressText);
        Assert.Equal(0d, viewModel.ProgressFraction, 10);
        Assert.Equal(2, viewModel.CompletedCount);

        Assert.Null(await _study.AnswerAsync(firstCard, Grade.Again));
        var insights = await _db.GetInsightsAsync(DateTime.UtcNow);
        Assert.Equal(2, insights.ReviewsToday);

        var returnedPage = new LearnViewModel(_services);
        await returnedPage.ResumeAsync();
        Assert.Equal($"0 / {plan.TotalCards}", returnedPage.ProgressText);
        Assert.Equal(0d, returnedPage.ProgressFraction, 10);
        Assert.Equal(2, returnedPage.CompletedCount);
        Assert.Equal(viewModel.Current!.CardId, returnedPage.Current!.CardId);
        Assert.Equal(2, (await _db.GetInsightsAsync(DateTime.UtcNow)).ReviewsToday);
    }

    [Fact]
    public async Task EndCommandCanEndAPausedSessionWithoutCreatingAReview()
    {
        await _study.StartAsync(Intensity.Normal);
        var viewModel = new LearnViewModel(_services);
        await viewModel.ResumeAsync();
        viewModel.RevealCommand.Execute(null);

        var pauseProperty = typeof(LearnViewModel).GetProperty("PauseCommand");
        Assert.NotNull(pauseProperty);
        var pauseCommand = Assert.IsAssignableFrom<IAsyncRelayCommand>(pauseProperty.GetValue(viewModel));
        await pauseCommand.ExecuteAsync(null);

        await viewModel.EndCommand.ExecuteAsync(null);

        Assert.False(_study.IsActive);
        Assert.False((bool)typeof(LearnViewModel).GetProperty("IsPaused")!.GetValue(viewModel)!);
        Assert.True(viewModel.Done);
        Assert.Equal(0, (await _db.GetInsightsAsync(DateTime.UtcNow)).ReviewsToday);
    }

    [Fact]
    public async Task SessionTransitionsNotifyCommandsAndKeepCanExecuteInSync()
    {
        await _study.StartAsync(Intensity.Normal);
        var viewModel = new LearnViewModel(_services);
        int pauseNotifications = 0;
        int resumeNotifications = 0;
        int endNotifications = 0;
        viewModel.PauseCommand.CanExecuteChanged += (_, _) => pauseNotifications++;
        viewModel.ResumeCommand.CanExecuteChanged += (_, _) => resumeNotifications++;
        viewModel.EndCommand.CanExecuteChanged += (_, _) => endNotifications++;

        Assert.False(viewModel.PauseCommand.CanExecute(null));
        Assert.False(viewModel.ResumeCommand.CanExecute(null));
        Assert.False(viewModel.EndCommand.CanExecute(null));

        await viewModel.ResumeAsync();

        Assert.True(viewModel.RevealCommand.CanExecute(null));
        Assert.False(viewModel.GradeCommand.CanExecute("3"));
        Assert.True(viewModel.PauseCommand.CanExecute(null));
        Assert.False(viewModel.ResumeCommand.CanExecute(null));
        Assert.True(viewModel.EndCommand.CanExecute(null));
        Assert.True(pauseNotifications > 0);
        Assert.True(resumeNotifications > 0);
        Assert.True(endNotifications > 0);

        viewModel.RevealCommand.Execute(null);
        Assert.False(viewModel.RevealCommand.CanExecute(null));
        Assert.True(viewModel.GradeCommand.CanExecute("3"));

        await viewModel.PauseCommand.ExecuteAsync(null);
        Assert.True(viewModel.PauseCommand.CanExecute(null));
        Assert.True(viewModel.ResumeCommand.CanExecute(null));
        Assert.True(viewModel.EndCommand.CanExecute(null));
        Assert.False(viewModel.GradeCommand.CanExecute("3"));
        Assert.True(resumeNotifications > 1);
        Assert.True(endNotifications > 1);

        await viewModel.ResumeCommand.ExecuteAsync(null);
        Assert.True(viewModel.PauseCommand.CanExecute(null));
        Assert.False(viewModel.ResumeCommand.CanExecute(null));
        Assert.True(viewModel.EndCommand.CanExecute(null));
        Assert.True(viewModel.GradeCommand.CanExecute("3"));

        for (int i = 0; i < 2; i++)
        {
            int previousResumeNotifications = resumeNotifications;
            int previousEndNotifications = endNotifications;

            await viewModel.PauseCommand.ExecuteAsync(null);
            Assert.True(viewModel.ResumeCommand.CanExecute(null));
            await viewModel.ResumeCommand.ExecuteAsync(null);
            Assert.True(viewModel.PauseCommand.CanExecute(null));

            Assert.True(resumeNotifications > previousResumeNotifications);
            Assert.True(endNotifications > previousEndNotifications);
        }

        await viewModel.PauseCommand.ExecuteAsync(null);
        Assert.True(viewModel.EndCommand.CanExecute(null));
        await viewModel.EndCommand.ExecuteAsync(null);

        Assert.True(viewModel.Done);
        Assert.False(viewModel.InSession);
        Assert.False(viewModel.IsPaused);
        Assert.False(viewModel.PauseCommand.CanExecute(null));
        Assert.False(viewModel.ResumeCommand.CanExecute(null));
        Assert.False(viewModel.EndCommand.CanExecute(null));
        Assert.Equal(0, (await _db.GetInsightsAsync(DateTime.UtcNow)).ReviewsToday);
    }

    [Fact]
    public async Task DoneTransitionNotifiesCommandsWhoseAvailabilityDependsOnIt()
    {
        await _study.StartAsync(Intensity.Normal);
        var viewModel = new LearnViewModel(_services);
        await viewModel.ResumeAsync();
        int pauseNotifications = 0;
        viewModel.PauseCommand.CanExecuteChanged += (_, _) => pauseNotifications++;

        Assert.True(viewModel.PauseCommand.CanExecute(null));
        viewModel.Done = true;

        Assert.False(viewModel.PauseCommand.CanExecute(null));
        Assert.True(pauseNotifications > 0);
    }

    [Fact]
    public async Task WindowDeactivationDuringPauseDoesNotSubtractTheSameTimeTwice()
    {
        var clock = new AdjustableTimeProvider();
        _study = new StudyService(_db, new FsrsCoreEngine(enableFuzzing: false), clock);
        _services = new AppServices(_study);
        await _study.StartAsync(Intensity.Normal);

        var viewModel = new LearnViewModel(_services);
        await viewModel.ResumeAsync();
        var card = Assert.IsType<StudyItem>(viewModel.Current);
        viewModel.RevealCommand.Execute(null);

        clock.Advance(TimeSpan.FromSeconds(5));
        await viewModel.PauseCommand.ExecuteAsync(null);
        clock.Advance(TimeSpan.FromSeconds(4));
        viewModel.NotifyUnfocused(3_000);
        clock.Advance(TimeSpan.FromSeconds(1));
        await viewModel.ResumeCommand.ExecuteAsync(null);
        clock.Advance(TimeSpan.FromSeconds(10));
        await viewModel.GradeCommand.ExecuteAsync("3");

        var sample = Assert.Single(await _db.GetRecentCardReviewHistoryAsync(card.CardId));
        Assert.Equal(15_000, sample.EffectiveElapsedMs);
        Assert.True(sample.TimingValid);
    }

    public async Task DisposeAsync()
    {
        if (_study.IsActive)
            await _study.FinishAsync();
        await _db.DisposeAsync();
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    private sealed class AdjustableTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }
}
