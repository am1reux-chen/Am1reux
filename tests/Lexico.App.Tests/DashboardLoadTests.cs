using Lexio.Services;
using Lexio.ViewModels;
using Lexico.Core.Data;
using Lexico.Core.Dictionary;
using Lexico.Core.Scheduling;
using Lexico.Core.Study;
using Xunit;

namespace Lexico.App.Tests;

public sealed class DashboardLoadTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lexio-dashboard-load-" + Guid.NewGuid().ToString("N"));
    private AppDatabase _db = null!;
    private AppServices _services = null!;

    public Task InitializeAsync()
    {
        _db = new AppDatabase(Path.Combine(_directory, "uninitialized.db"));
        _services = new AppServices(_db, new StudyService(_db, new FsrsCoreEngine(enableFuzzing: false)));
        return Task.CompletedTask;
    }

    [Fact]
    public async Task TodayLoadFailureDoesNotEscapeOrLeaveLoadingActive()
    {
        var errors = new List<Exception>();
        var viewModel = new TodayViewModel(_services, errors.Add);

        var error = await Record.ExceptionAsync(viewModel.LoadAsync);

        Assert.Null(error);
        Assert.False(viewModel.Loading);
        Assert.Equal("今日计划加载失败，请重试。", viewModel.LoadError);
        Assert.Single(errors);

        await _db.MigrateAsync();
        await new DictionaryService(_db).EnsureStarterDeckAsync();
        await viewModel.RetryCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.LoadError);
        Assert.False(viewModel.Loading);
        Assert.True(viewModel.CanStart);
        Assert.Equal(3, viewModel.Options.Count);
    }

    [Fact]
    public async Task InsightsLoadFailureDoesNotEscapeOrLeaveLoadingActive()
    {
        var errors = new List<Exception>();
        var viewModel = new InsightsViewModel(_services, errors.Add);

        var error = await Record.ExceptionAsync(viewModel.LoadAsync);

        Assert.Null(error);
        Assert.False(viewModel.Loading);
        Assert.Equal("Insights 数据加载失败，请重试。", viewModel.LoadError);
        Assert.Single(errors);

        await _db.MigrateAsync();
        await new DictionaryService(_db).EnsureStarterDeckAsync();
        await viewModel.RetryCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.LoadError);
        Assert.False(viewModel.Loading);
        Assert.Equal(7, viewModel.Bars.Count);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
