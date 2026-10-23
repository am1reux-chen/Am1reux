using System.IO.Compression;
using System.Text;
using Lexio.Services;
using Lexio.ViewModels;
using Lexico.Core.Data;
using Lexico.Core.Domain;
using Xunit;

namespace Lexico.App.Tests;

public sealed class LibraryViewModelTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lexio-library-viewmodel-" + Guid.NewGuid().ToString("N"));
    private AppDatabase _db = null!;
    private AppServices _services = null!;

    [Fact]
    public void SearchResultPronunciationIncludesAmericanAndBritishForms()
    {
        var result = new SearchResultItem(
            new(1, "color", "/ˈkʌlɚ/", "/ˈkʌlə/", null, null, []), "颜色");

        Assert.Equal("美 /ˈkʌlɚ/　英 /ˈkʌlə/", result.PronunciationDisplay);
    }

    [Fact]
    public void SearchResultStillShowsBritishPronunciationWhenAmericanIsMissing()
    {
        var result = new SearchResultItem(
            new(1, "colour", null, "/ˈkʌlə/", null, null, []), "颜色");

        Assert.Equal("英 /ˈkʌlə/", result.PronunciationDisplay);
    }

    public async Task InitializeAsync()
    {
        _db = new AppDatabase(Path.Combine(_directory, "library.db"));
        await _db.MigrateAsync();
        await _db.UpsertWordAsync(new(0, "curious", null, null, null, null, [new(0, "adj.", "好奇的", null, null, null)]));
        _services = new AppServices(_db);
    }

    [Fact]
    public async Task ClearingQueryDiscardsTheEarlierSearchResultThatWasWaitingForUiDispatch()
    {
        var viewModel = new LibraryViewModel(_services);
        var staleResultReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStaleResult = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int dispatchCount = 0;

        _services.ActionDispatcher = async action =>
        {
            if (Interlocked.Increment(ref dispatchCount) == 1)
            {
                staleResultReady.SetResult();
                await releaseStaleResult.Task;
            }
            action();
        };

        var earlierSearch = viewModel.SearchAsync("curious");
        await staleResultReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await viewModel.SearchAsync("");
        releaseStaleResult.SetResult();
        await earlierSearch;

        Assert.Empty(viewModel.Results);
        Assert.False(viewModel.NoResults);
        Assert.Empty(viewModel.ResultCountText);
    }

    [Fact]
    public async Task ClearingQueryStopsTheProgressIndicatorWhileEarlierSearchIsPending()
    {
        var viewModel = new LibraryViewModel(_services);
        var staleResultReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStaleResult = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int dispatchCount = 0;
        _services.ActionDispatcher = async action =>
        {
            if (Interlocked.Increment(ref dispatchCount) == 1)
            {
                staleResultReady.SetResult();
                await releaseStaleResult.Task;
            }
            action();
        };

        var earlierSearch = viewModel.SearchAsync("curious");
        await staleResultReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await viewModel.SearchAsync("");
            Assert.False(viewModel.Searching);
        }
        finally
        {
            releaseStaleResult.SetResult();
            await earlierSearch;
        }
    }

    [Fact]
    public async Task SearchFailureClearsStaleResultsStopsLoadingAndShowsRetryStatus()
    {
        await using var uninitializedDb = new AppDatabase(Path.Combine(_directory, "uninitialized.db"));
        var reportedErrors = new List<Exception>();
        var viewModel = new LibraryViewModel(new AppServices(uninitializedDb), reportedErrors.Add);
        viewModel.Results.Add(new SearchResultItem(new(1, "curious", null, null, null, null, []), "旧释义"));

        var error = await Record.ExceptionAsync(() => viewModel.SearchAsync("curious"));

        Assert.Null(error);
        Assert.Empty(viewModel.Results);
        Assert.False(viewModel.NoResults);
        Assert.False(viewModel.Searching);
        Assert.Equal("搜索失败，请重试。", viewModel.ResultCountText);
        Assert.Single(reportedErrors);
    }

    [Fact]
    public async Task DeckLoadFailureShowsRetryStatusAndRetryLoadsDecks()
    {
        await using var uninitializedDb = new AppDatabase(Path.Combine(_directory, "uninitialized-decks.db"));
        var reportedErrors = new List<Exception>();
        var services = new AppServices(uninitializedDb);
        var viewModel = new LibraryViewModel(services, reportedErrors.Add);
        viewModel.Decks.Add(new DeckInfo(9, "Existing", null, null, false, 3));

        var error = await Record.ExceptionAsync(viewModel.LoadDecksForDisplayAsync);

        Assert.Null(error);
        Assert.Equal("Existing", Assert.Single(viewModel.Decks).Name);
        Assert.Equal("词书列表加载失败，请重试。", viewModel.DecksLoadError);
        Assert.Single(reportedErrors);

        await uninitializedDb.MigrateAsync();
        await services.Dictionary.EnsureStarterDeckAsync();
        await viewModel.RetryDecksCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.DecksLoadError);
        Assert.True(Assert.Single(viewModel.Decks).BuiltIn);
    }

    [Fact]
    public async Task SelectingDeckLoadsItsWordsAndLimitsSubsequentSearches()
    {
        var appleId = await _db.UpsertWordAsync(new(0, "apple", null, null, null, null, []));
        var applyId = await _db.UpsertWordAsync(new(0, "apply", null, null, null, null, []));
        var ieltsDeckId = await _db.EnsureDeckAsync("IELTS", null, "test", builtIn: true);
        var universityDeckId = await _db.EnsureDeckAsync("University", null, "test", builtIn: true);
        await _db.AddWordToDeckAsync(ieltsDeckId, appleId, 0);
        await _db.AddWordToDeckAsync(universityDeckId, applyId, 0);
        await _db.EnsureCardsForDeckAsync(ieltsDeckId);
        await _db.EnsureCardsForDeckAsync(universityDeckId);
        var viewModel = new LibraryViewModel(_services);
        await viewModel.LoadDecksAsync();
        var ieltsDeck = viewModel.Decks.Single(deck => deck.Id == ieltsDeckId);

        await viewModel.SelectDeckAsync(ieltsDeck);

        Assert.Same(ieltsDeck, viewModel.SelectedDeck);
        Assert.Equal("搜索范围：IELTS（1 词）", viewModel.SearchScopeText);
        Assert.Equal(["apple"], viewModel.Results.Select(item => item.Word.Lemma));

        await viewModel.SearchAsync("app");

        Assert.Equal(["apple"], viewModel.Results.Select(item => item.Word.Lemma));
    }

    [Fact]
    public async Task LibrarySearchExposesImportedRichWordDetailsForTheDetailsExpander()
    {
        var wordId = await _db.UpsertWordAsync(new WordEntry(0, "parity", "/ˈperəti/", null, null, null,
            [new(0, "n.", "平等", null, null, null)])
        {
            Details = new WordDetails([new("We checked parity.", "我们检查了一致性。")], [], [], [], null, []),
        });
        var deckId = await _db.EnsureDeckAsync("Parity", null, "test", builtIn: false);
        await _db.AddWordToDeckAsync(deckId, wordId, 0);
        await _db.EnsureCardsForDeckAsync(deckId);

        var viewModel = new LibraryViewModel(_services);
        await viewModel.SearchAsync("parity");

        var result = Assert.Single(viewModel.Results);
        var section = Assert.Single(result.DetailSections);
        Assert.Equal("例句", section.Title);
        Assert.Equal("We checked parity.", Assert.Single(section.Lines).Primary);
    }

    [Fact]
    public async Task ImportingAWordRefreshesTheCurrentSearchResults()
    {
        var csvPath = Path.Combine(_directory, "serendipity.csv");
        await File.WriteAllTextAsync(csvPath, "word,zh\nserendipity,意外发现的乐趣\n");
        var viewModel = new LibraryViewModel(_services) { Query = "serendipity" };

        await viewModel.SearchAsync(viewModel.Query);
        Assert.Empty(viewModel.Results);
        Assert.True(viewModel.NoResults);

        await viewModel.ImportCommand.ExecuteAsync(csvPath);

        Assert.Contains(viewModel.Results, item => item.Word.Lemma == "serendipity");
        Assert.False(viewModel.NoResults);
        Assert.Contains("新增 1", viewModel.StatusText);
    }

    [Fact]
    public async Task SuccessfulImportExposesAUserVisibleSummaryWithCounts()
    {
        var csvPath = Path.Combine(_directory, "serendipity.csv");
        await File.WriteAllTextAsync(csvPath, "word,zh\nserendipity,意外发现的乐趣\n");
        var viewModel = new LibraryViewModel(_services);

        await viewModel.ImportCommand.ExecuteAsync(csvPath);

        Assert.Equal(new LibraryImportFeedback("导入完成", "导入完成：「serendipity」新增 1，更新 0，跳过 0"),
            viewModel.LastImportFeedback);
    }

    [Fact]
    public async Task ImportingTypeWordsZipUsesTheCatalogNameInTheLibrary()
    {
        var archivePath = Path.Combine(_directory, "TypeWords-master.zip");
        Directory.CreateDirectory(_directory);
        using (var archiveStream = File.Create(archivePath))
        using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create))
        {
            AddZipEntry(archive, "TypeWords-master/public/list/recommend_word.json",
                "[{\"name\":\"CET-4\",\"url\":\"CET4.json\",\"recommended\":true}]");
            AddZipEntry(archive, "TypeWords-master/public/dicts/en/word/CET4.json",
                "[{\"word\":\"cancel\",\"trans\":[{\"pos\":\"v.\",\"cn\":\"取消\"}]}]");
        }
        var viewModel = new LibraryViewModel(_services);

        await viewModel.ImportCommand.ExecuteAsync(archivePath);

        Assert.Equal(new LibraryImportFeedback("导入完成", "导入完成：「CET-4」新增 1，更新 0，跳过 0"),
            viewModel.LastImportFeedback);
        Assert.Contains(viewModel.Decks, deck => deck.Name == "CET-4");
    }

    [Fact]
    public async Task RejectedImportExposesTheReadableImportError()
    {
        var csvPath = Path.Combine(_directory, "invalid.csv");
        await File.WriteAllTextAsync(csvPath, "!!!,不合法的单词\n");
        var viewModel = new LibraryViewModel(_services);

        await viewModel.ImportCommand.ExecuteAsync(csvPath);

        Assert.Equal(new LibraryImportFeedback("导入失败", "导入失败：没有可导入的有效单词"),
            viewModel.LastImportFeedback);
    }

    [Fact]
    public async Task ImportReadFailureShowsSafeFeedbackAndReportsTheTechnicalError()
    {
        var missingPath = Path.Combine(_directory, "missing.csv");
        var reportedErrors = new List<Exception>();
        var viewModel = new LibraryViewModel(_services, reportedErrors.Add);

        await viewModel.ImportCommand.ExecuteAsync(missingPath);

        Assert.Equal(new LibraryImportFeedback("导入失败",
                "导入失败：无法读取文件或写入词库。请检查文件访问权限并查看诊断日志。"),
            viewModel.LastImportFeedback);
        Assert.IsType<FileNotFoundException>(Assert.Single(reportedErrors));
        Assert.DoesNotContain(missingPath, viewModel.LastImportFeedback!.Message);
    }

    private static void AddZipEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
    }
}
