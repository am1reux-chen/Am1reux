using Lexio.Services;
using Lexio.ViewModels;
using Lexico.Core.Data;
using Lexico.Core.Domain;
using Lexico.Core.Scheduling;
using Lexico.Core.Study;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Lexico.App.Tests;

public sealed class WeeklyStudyWorkflowTests : IAsyncLifetime
{
    private const int WordCount = 30;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lexio-weekly-workflow-" + Guid.NewGuid().ToString("N"));
    private string _databasePath = null!;
    private AppDatabase _db = null!;
    private StudyService _study = null!;
    private AppServices _services = null!;
    private long _deckId;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(_directory, "weekly.db");
        _db = new AppDatabase(_databasePath);
        await _db.MigrateAsync();
        _study = new StudyService(_db, new FsrsCoreEngine(enableFuzzing: false));
        _services = new AppServices(_db, _study);
        _deckId = await _db.EnsureDeckAsync("IELTS Weekly", null, "test", builtIn: true);

        for (int index = 0; index < WordCount; index++)
        {
            var wordId = await _db.UpsertWordAsync(new(
                0,
                $"lexeme{index:D2}",
                null,
                null,
                null,
                null,
                [new(0, "n.", $"释义 {index + 1}", null, null, null)]));
            await _db.AddWordToDeckAsync(_deckId, wordId, index);
        }

        await _db.EnsureCardsForDeckAsync(_deckId);
    }

    [Fact]
    public async Task CanCompleteSevenDailySessionsInOneStudyBook()
    {
        var plannedCardsByDay = new List<int>();
        var reviewedCardsByDay = new List<int>();

        for (int day = 1; day <= 7; day++)
        {
            if (day > 1)
            {
                // Simulate the app being reopened on each new study day.
                _study = new StudyService(_db, new FsrsCoreEngine(enableFuzzing: false));
                _services = new AppServices(_db, _study);
                await _services.LoadUserPreferencesAsync();
            }

            await MakeDeckCardsDueAsync();

            var today = new TodayViewModel(_services);
            await today.LoadAsync();
            var deck = Assert.Single(today.DeckChoices, choice => choice.Id == _deckId);
            await today.SelectDeckAsync(deck);
            Assert.Equal(_deckId, today.SelectedDeckChoice.Id);

            var option = Assert.Single(today.Options, item => item.Intensity == Intensity.Normal);
            Assert.True(option.TotalCards > 0, $"Day {day} should produce a nonempty plan.");
            Assert.Equal(_deckId, option.DeckId);
            plannedCardsByDay.Add(option.TotalCards);

            await today.StartCommand.ExecuteAsync(option);
            Assert.Equal(option.TotalCards, _study.CurrentPlan?.TotalCards);

            var learn = new LearnViewModel(_services) { FourGradeMode = true };
            await learn.ResumeAsync();
            int answers = 0;
            while (!learn.Done)
            {
                Assert.True(learn.ShowCard, $"Day {day} stopped before the plan queue drained.");
                Assert.NotNull(learn.Current);
                learn.RevealCommand.Execute(null);
                await learn.GradeCommand.ExecuteAsync("4");
                answers++;
                Assert.True(answers <= option.TotalCards, $"Day {day} repeated a planned card unexpectedly.");
            }

            Assert.Equal(option.TotalCards, answers);
            Assert.Equal(option.TotalCards, learn.CompletedCount);
            Assert.Equal(0, learn.RemainingCount);
            Assert.Equal($"{option.TotalCards} / {option.TotalCards}", learn.ProgressText);
            Assert.Equal(1d, learn.ProgressFraction, 10);
            Assert.Equal("今日计划已完成", learn.SummaryText);
            Assert.DoesNotContain("队列剩余", learn.SummaryDetail);
            reviewedCardsByDay.Add(answers);

            // A new Today page represents returning from Learn after this day's session.
            var returnedToday = new TodayViewModel(_services);
            await returnedToday.LoadAsync();
            Assert.Equal(_deckId, returnedToday.SelectedDeckChoice.Id);
            Assert.All(returnedToday.Options, item => Assert.Equal(_deckId, item.DeckId));
        }

        Assert.Equal(7, plannedCardsByDay.Count);
        Assert.All(reviewedCardsByDay, reviewed => Assert.True(reviewed > 0));
        Assert.Equal(WordCount, await CountDeckCardsAsync());
        Assert.Equal(7, await CountFinishedSessionsAsync());
        Assert.Equal(reviewedCardsByDay.Sum(), await CountReviewLogsAsync());
        Assert.True(plannedCardsByDay[^1] >= plannedCardsByDay[0]);
    }

    private async Task MakeDeckCardsDueAsync()
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE cards SET due=$due WHERE deck_id=$deckId";
        command.Parameters.AddWithValue("$due", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$deckId", _deckId);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<int> CountDeckCardsAsync()
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM cards WHERE deck_id=$deckId";
        command.Parameters.AddWithValue("$deckId", _deckId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<int> CountFinishedSessionsAsync()
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sessions WHERE ended_at IS NOT NULL";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<int> CountReviewLogsAsync()
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM review_logs";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<SqliteConnection> OpenConnectionAsync()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    public async Task DisposeAsync()
    {
        if (_study.IsActive)
            await _study.FinishAsync();
        await _db.DisposeAsync();
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
