using Lexio.Services;
using Lexio.ViewModels;
using Lexico.Core.Data;
using Lexico.Core.Dictionary;
using Lexico.Core.Scheduling;
using Lexico.Core.Study;
using Xunit;

namespace Lexico.App.Tests;

public sealed class TodayStartFailureTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lexio-today-start-" + Guid.NewGuid().ToString("N"));
    private AppDatabase _db = null!;
    private AppServices _services = null!;
    private bool _dbDisposed;

    public async Task InitializeAsync()
    {
        _db = new AppDatabase(Path.Combine(_directory, "today.db"));
        await _db.MigrateAsync();
        await new DictionaryService(_db).EnsureStarterDeckAsync();
        _services = new AppServices(_db, new StudyService(_db, new FsrsCoreEngine(enableFuzzing: false)));
    }

    [Fact]
    public async Task StartFailureIsShownAndRetryRepeatsTheStartOperation()
    {
        var errors = new List<Exception>();
        var viewModel = new TodayViewModel(_services, errors.Add);
        await viewModel.LoadAsync();
        var option = Assert.Single(viewModel.Options, item => item.Intensity == Lexico.Core.Domain.Intensity.Normal);
        await _db.DisposeAsync();
        _dbDisposed = true;

        var startError = await Record.ExceptionAsync(() => viewModel.StartCommand.ExecuteAsync(option));

        Assert.Null(startError);
        Assert.False(viewModel.Loading);
        Assert.True(viewModel.CanStart);
        Assert.Equal("学习启动失败，请重试。", viewModel.LoadError);
        Assert.Single(errors);

        await viewModel.RetryCommand.ExecuteAsync(null);

        Assert.Equal("学习启动失败，请重试。", viewModel.LoadError);
        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public async Task SuccessfulStartRaisesSessionStartedAfterRestoringUiState()
    {
        var viewModel = new TodayViewModel(_services);
        await viewModel.LoadAsync();
        int started = 0;
        viewModel.SessionStarted += () => started++;

        var option = Assert.Single(viewModel.Options, item => item.Intensity == Lexico.Core.Domain.Intensity.Normal);
        await viewModel.StartCommand.ExecuteAsync(option);

        Assert.Equal(1, started);
        Assert.True(_services.Study.IsActive);
        Assert.False(viewModel.Loading);
        Assert.True(viewModel.CanStart);
        Assert.Empty(viewModel.LoadError);
    }

    public async Task DisposeAsync()
    {
        if (!_dbDisposed)
            await _db.DisposeAsync();
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
