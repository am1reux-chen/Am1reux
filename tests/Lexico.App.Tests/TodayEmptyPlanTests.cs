using Lexio.Services;
using Lexio.ViewModels;
using Lexico.Core.Data;
using Lexico.Core.Dictionary;
using Lexico.Core.Domain;
using Lexico.Core.Scheduling;
using Lexico.Core.Study;
using Xunit;

namespace Lexico.App.Tests;

public sealed class TodayEmptyPlanTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lexio-today-empty-" + Guid.NewGuid().ToString("N"));
    private AppDatabase _db = null!;
    private AppServices _services = null!;

    public async Task InitializeAsync()
    {
        _db = new AppDatabase(Path.Combine(_directory, "today.db"));
        await _db.MigrateAsync();
        _services = new AppServices(_db, new StudyService(_db, new FsrsCoreEngine(enableFuzzing: false)));
    }

    [Fact]
    public async Task EmptyPlansCannotStartAZeroCardSession()
    {
        var viewModel = new TodayViewModel(_services);
        int started = 0;
        viewModel.SessionStarted += () => started++;

        await viewModel.LoadAsync();

        Assert.False(viewModel.Loading);
        Assert.Equal(3, viewModel.Options.Count);
        Assert.All(viewModel.Options, option => Assert.Equal("无任务", option.MinutesText));
        Assert.True(viewModel.NoTasks);
        Assert.False(viewModel.CanStart);
        Assert.All(viewModel.Options, option => Assert.False(viewModel.StartCommand.CanExecute(option)));

        await viewModel.StartCommand.ExecuteAsync(viewModel.Options[1]);

        Assert.False(_services.Study.IsActive);
        Assert.Equal(0, started);
    }

    [Fact]
    public async Task LightIntensityCanStartWhenOnlyNewWordsAreAvailable()
    {
        await new DictionaryService(_db).EnsureStarterDeckAsync();
        var viewModel = new TodayViewModel(_services);

        await viewModel.LoadAsync();

        var lightOption = Assert.Single(viewModel.Options, option => option.Intensity == Lexico.Core.Domain.Intensity.Light);
        var normalOption = Assert.Single(viewModel.Options, option => option.Intensity == Lexico.Core.Domain.Intensity.Normal);
        Assert.True(viewModel.CanStart);
        Assert.False(viewModel.NoTasks);
        Assert.NotEqual("无任务", lightOption.MinutesText);
        Assert.Contains("新词", lightOption.NewText);
        Assert.True(lightOption.TotalCards > 0);
        Assert.True(viewModel.StartCommand.CanExecute(lightOption));
        Assert.True(viewModel.StartCommand.CanExecute(normalOption));

        await viewModel.StartCommand.ExecuteAsync(lightOption);

        Assert.True(_services.Study.IsActive);
        Assert.Equal(lightOption.TotalCards, _services.Study.CurrentPlan?.TotalCards);
    }

    [Fact]
    public async Task SelectingAStudyBookLimitsPlanAndStartedSessionToThatBook()
    {
        var appleId = await _db.UpsertWordAsync(new(0, "apple", null, null, null, null,
            [new(0, "n.", "苹果", null, null, null)]));
        var planetId = await _db.UpsertWordAsync(new(0, "planet", null, null, null, null,
            [new(0, "n.", "行星", null, null, null)]));
        var ieltsDeckId = await _db.EnsureDeckAsync("IELTS", null, "test", builtIn: true);
        var universityDeckId = await _db.EnsureDeckAsync("University", null, "test", builtIn: true);
        await _db.AddWordToDeckAsync(ieltsDeckId, appleId, 0);
        await _db.AddWordToDeckAsync(universityDeckId, planetId, 0);
        await _db.EnsureCardsForDeckAsync(ieltsDeckId);
        await _db.EnsureCardsForDeckAsync(universityDeckId);

        var viewModel = new TodayViewModel(_services);
        await viewModel.LoadAsync();

        Assert.Equal("全部词书", viewModel.SelectedDeckChoice?.Name);
        Assert.Equal(2, Assert.Single(viewModel.Options, option => option.Intensity == Intensity.Normal).TotalCards);

        var ielts = Assert.Single(viewModel.DeckChoices, choice => choice.Id == ieltsDeckId);
        await viewModel.SelectDeckAsync(ielts);

        var selectedPlan = Assert.Single(viewModel.Options, option => option.Intensity == Intensity.Normal);
        Assert.Equal(ieltsDeckId, selectedPlan.DeckId);
        Assert.Equal(1, selectedPlan.TotalCards);
        Assert.Contains("IELTS", viewModel.DueText);
        Assert.Contains("待学新词 1", viewModel.DueText);

        await viewModel.StartCommand.ExecuteAsync(selectedPlan);

        Assert.Equal(1, _services.Study.CurrentPlan?.TotalCards);
        Assert.Equal("apple", _services.Study.PeekNext()?.Lemma);
    }

    [Fact]
    public async Task SelectedStudyBookSurvivesReturningToToday()
    {
        var appleId = await _db.UpsertWordAsync(new(0, "apple", null, null, null, null,
            [new(0, "n.", "苹果", null, null, null)]));
        var planetId = await _db.UpsertWordAsync(new(0, "planet", null, null, null, null,
            [new(0, "n.", "行星", null, null, null)]));
        var ieltsDeckId = await _db.EnsureDeckAsync("IELTS", null, "test", builtIn: true);
        var universityDeckId = await _db.EnsureDeckAsync("University", null, "test", builtIn: true);
        await _db.AddWordToDeckAsync(ieltsDeckId, appleId, 0);
        await _db.AddWordToDeckAsync(universityDeckId, planetId, 0);
        await _db.EnsureCardsForDeckAsync(ieltsDeckId);
        await _db.EnsureCardsForDeckAsync(universityDeckId);

        var firstTodayPage = new TodayViewModel(_services);
        await firstTodayPage.LoadAsync();
        var ielts = Assert.Single(firstTodayPage.DeckChoices, choice => choice.Id == ieltsDeckId);
        await firstTodayPage.SelectDeckAsync(ielts);
        await firstTodayPage.StartCommand.ExecuteAsync(
            Assert.Single(firstTodayPage.Options, option => option.Intensity == Intensity.Normal));
        await _services.Study.FinishAsync();

        var returnedTodayPage = new TodayViewModel(_services);
        await returnedTodayPage.LoadAsync();

        Assert.Equal(ieltsDeckId, returnedTodayPage.SelectedDeckChoice.Id);
        Assert.All(returnedTodayPage.Options, option => Assert.Equal(ieltsDeckId, option.DeckId));
        Assert.Equal(1, Assert.Single(returnedTodayPage.Options, option => option.Intensity == Intensity.Normal).TotalCards);

        var restartedServices = new AppServices(_db, _services.Study);
        await restartedServices.LoadUserPreferencesAsync();
        var afterRestart = new TodayViewModel(restartedServices);
        await afterRestart.LoadAsync();

        Assert.Equal(ieltsDeckId, afterRestart.SelectedDeckChoice.Id);
        Assert.All(afterRestart.Options, option => Assert.Equal(ieltsDeckId, option.DeckId));
    }

    [Fact]
    public async Task FailedStudyBookSaveRestoresPreviousSelectionAndShowsAnError()
    {
        var appleId = await _db.UpsertWordAsync(new(0, "apple", null, null, null, null,
            [new(0, "n.", "苹果", null, null, null)]));
        var planetId = await _db.UpsertWordAsync(new(0, "planet", null, null, null, null,
            [new(0, "n.", "行星", null, null, null)]));
        var ieltsDeckId = await _db.EnsureDeckAsync("IELTS", null, "test", builtIn: true);
        var universityDeckId = await _db.EnsureDeckAsync("University", null, "test", builtIn: true);
        await _db.AddWordToDeckAsync(ieltsDeckId, appleId, 0);
        await _db.AddWordToDeckAsync(universityDeckId, planetId, 0);
        await _db.EnsureCardsForDeckAsync(ieltsDeckId);
        await _db.EnsureCardsForDeckAsync(universityDeckId);

        var errors = new List<Exception>();
        var viewModel = new TodayViewModel(_services, errors.Add);
        await viewModel.LoadAsync();
        var ielts = Assert.Single(viewModel.DeckChoices, choice => choice.Id == ieltsDeckId);
        await viewModel.SelectDeckAsync(ielts);
        var university = Assert.Single(viewModel.DeckChoices, choice => choice.Id == universityDeckId);

        var insideUiDispatch = false;
        var onUiThread = true;
        var changesOutsideUiThread = new List<string?>();
        _services.ActionDispatcher = action =>
        {
            var previous = insideUiDispatch;
            insideUiDispatch = true;
            try { action(); }
            finally { insideUiDispatch = previous; }
            return Task.CompletedTask;
        };
        viewModel.PropertyChanged += (_, args) =>
        {
            if (!onUiThread && !insideUiDispatch)
                changesOutsideUiThread.Add(args.PropertyName);
        };
        _services.StudyDeckSave = async _ =>
        {
            await Task.Yield();
            throw new IOException("settings file is read-only");
        };

        onUiThread = true;
        var selection = viewModel.SelectDeckAsync(university);
        onUiThread = false;
        await selection;

        Assert.Empty(changesOutsideUiThread);
        Assert.Equal(ieltsDeckId, viewModel.SelectedDeckChoice.Id);
        Assert.All(viewModel.Options, option => Assert.Equal(ieltsDeckId, option.DeckId));
        Assert.Contains("词书选择保存失败", viewModel.LoadError);
        Assert.Single(errors);
    }

    [Fact]
    public async Task SelectingStudyBookKeepsRefreshPropertyChangesOnUiDispatcher()
    {
        var deckId = await _db.EnsureDeckAsync("IELTS", null, "test", builtIn: true);
        var viewModel = new TodayViewModel(_services);
        await viewModel.LoadAsync();

        var insideUiDispatch = false;
        var onUiThread = true;
        var changesOutsideUiThread = new List<string?>();
        _services.ActionDispatcher = action =>
        {
            var previous = insideUiDispatch;
            insideUiDispatch = true;
            try { action(); }
            finally { insideUiDispatch = previous; }
            return Task.CompletedTask;
        };
        _services.BeforeStudyDeckSave = async _ => await Task.Yield();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (!onUiThread && !insideUiDispatch)
                changesOutsideUiThread.Add(args.PropertyName);
        };

        onUiThread = true;
        var selection = viewModel.SelectDeckAsync(Assert.Single(viewModel.DeckChoices, item => item.Id == deckId));
        onUiThread = false;
        await selection;

        Assert.Empty(changesOutsideUiThread);
        Assert.Equal(deckId, viewModel.SelectedDeckChoice.Id);
        Assert.False(viewModel.Loading);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
