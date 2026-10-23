using Lexio.Services;
using Lexio.ViewModels;
using Lexico.Core.Data;
using Lexico.Core.Domain;
using Lexico.Core.Scheduling;
using Lexico.Core.Study;
using Xunit;

namespace Lexico.App.Tests;

public sealed class LearnPronunciationTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lexio-learn-ipa-" + Guid.NewGuid().ToString("N"));
    private AppDatabase _db = null!;
    private LearnViewModel _viewModel = null!;

    public async Task InitializeAsync()
    {
        _db = new AppDatabase(Path.Combine(_directory, "study.db"));
        await _db.MigrateAsync();
        var deckId = await _db.EnsureDeckAsync("IPA", null, null, builtIn: false);
        var wordId = await _db.UpsertWordAsync(new WordEntry(0, "color", "/ˈkʌlɚ/", "/ˈkʌlə/", null, null,
        [new WordSense(0, "n.", "颜色", null, null, null)]));
        await _db.AddWordToDeckAsync(deckId, wordId, 0);
        await _db.EnsureCardsForDeckAsync(deckId);

        var study = new StudyService(_db, new FsrsCoreEngine(enableFuzzing: false));
        await study.StartAsync(Intensity.Normal);
        _viewModel = new LearnViewModel(new AppServices(study));
        await _viewModel.ResumeAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void LearnViewModelDisplaysAmericanAndBritishPronunciation()
    {
        Assert.Equal("color", _viewModel.Lemma);
        Assert.Equal("/ˈkʌlɚ/", _viewModel.Ipa);
        Assert.Equal("/ˈkʌlə/", _viewModel.IpaUk);
        Assert.Equal("美 /ˈkʌlɚ/　英 /ˈkʌlə/", _viewModel.IpaDisplay);

        _viewModel.Ipa = "";
        Assert.Equal("英 /ˈkʌlə/", _viewModel.IpaDisplay);
    }
}
