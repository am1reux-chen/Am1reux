using System.Text.Json;
using Lexico.Core.Data;
using Lexico.Core.Dictionary;
using Lexico.Core.Domain;
using Lexico.Core.Scheduling;
using Lexico.Core.Study;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Lexico.Core.Tests;

public sealed class PersonalRecallPersistenceTests : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lexio-calibrator-" + Guid.NewGuid().ToString("N"));
    private readonly AppDatabase _db;

    public PersonalRecallPersistenceTests()
    {
        _db = new AppDatabase(Path.Combine(_directory, "calibrator.db"));
        _db.MigrateAsync().GetAwaiter().GetResult();
        new DictionaryService(_db).EnsureStarterDeckAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task StartTrainsEligibleShadowModelOnceAndSkipsUnchangedHistory()
    {
        var card = (await _db.GetPlanningCardsAsync(DateTime.UtcNow, DateTime.UtcNow.AddDays(1))).First().Card;
        await SeedPredictionsAsync(card.Id, 300);
        var study = new StudyService(_db, new FsrsCoreEngine(enableFuzzing: false));

        await study.StartAsync(Intensity.Light);
        var trained = await _db.GetActiveRecallModelProfileAsync();
        Assert.NotNull(trained);
        await study.FinishAsync();
        await SeedPredictionsAsync(card.Id, 49, 300);
        await study.StartAsync(Intensity.Light);
        var belowThreshold = await _db.GetActiveRecallModelProfileAsync();
        await study.FinishAsync();
        Assert.Equal(trained!.Id, belowThreshold!.Id);

        await SeedPredictionsAsync(card.Id, 1, 349);
        await study.StartAsync(Intensity.Light);
        var retrained = await _db.GetActiveRecallModelProfileAsync();
        await study.FinishAsync();

        Assert.Equal(2, trained!.Version);
        Assert.True(trained.Accepted);
        Assert.Equal(3, retrained!.Version);
        Assert.True(retrained.Accepted);
        Assert.Equal(3, await CountProfilesAsync());
    }

    [Fact]
    public async Task FailedPredictionInsertRollsBackReviewAndCardUpdate()
    {
        var card = (await _db.GetPlanningCardsAsync(DateTime.UtcNow, DateTime.UtcNow.AddDays(1))).First().Card;
        var updated = card with { Reps = card.Reps + 1, DueUtc = card.DueUtc.AddDays(2) };
        var log = new ReviewLogRow(
            0, card.Id, DateTime.UtcNow, Grade.Good, card.State, card.Step, card.Stability, card.Difficulty,
            0, card.Reps, 500, 500, true, null, "test", updated.DueUtc, 2);

        await Assert.ThrowsAsync<SqliteException>(() => _db.AnswerCardWithPredictionAsync(
            updated, log, new RecallPredictionRecord(long.MaxValue, 1, .5, .5, "{}")));

        var unchanged = await _db.GetCardAsync(card.Id);
        Assert.Equal(card.Reps, unchanged!.Reps);
        Assert.Equal(card.DueUtc, unchanged.DueUtc);
        Assert.Equal(0, await CountReviewsAsync(card.Id));
    }

    [Fact]
    public async Task TrainingSnapshotReconstructsFeaturesForLegacyReviewLogs()
    {
        var card = (await _db.GetPlanningCardsAsync(DateTime.UtcNow, DateTime.UtcNow.AddDays(1))).First().Card;
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "calibrator.db"),
        }.ToString()))
        {
            await connection.OpenAsync();
            var start = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            for (int index = 0; index < 60; index++)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO review_logs(card_id, reviewed_at, rating, state_before, scheduled_days_before,
                                            reps_before, elapsed_ms, timing_valid, fsrs_retrievability,
                                            engine_version, next_due, next_interval_days)
                    VALUES($card, $reviewed, $rating, 1, 4, $reps, 1200, 1, .8, 'legacy', $next, 4)
                    """;
                command.Parameters.AddWithValue("$card", card.Id);
                command.Parameters.AddWithValue("$reviewed", start.AddDays(index).ToString("O"));
                command.Parameters.AddWithValue("$rating", index == 0 ? (int)Grade.Again : (int)Grade.Good);
                command.Parameters.AddWithValue("$reps", index);
                command.Parameters.AddWithValue("$next", start.AddDays(index + 4).ToString("O"));
                await command.ExecuteNonQueryAsync();
            }
        }

        var snapshot = await _db.GetRecallTrainingSnapshotAsync();

        Assert.Equal(60, snapshot.Observations.Count);
        Assert.All(snapshot.Observations, row => Assert.NotEqual("{}", row.FeaturesJson));
        var latestFeatures = JsonSerializer.Deserialize<PersonalRecallCalibrator.FeatureVector>(
            snapshot.Observations[^1].FeaturesJson)!;
        Assert.Equal(.8, latestFeatures.FsrsProbability, 3);
        Assert.Equal(1, latestFeatures.RecentAccuracy);
        Assert.True(latestFeatures.HistoricalLapses > 0);
    }

    private async Task SeedPredictionsAsync(long cardId, int count, int offset = 0)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "calibrator.db"),
        }.ToString());
        await connection.OpenAsync();
        var features = JsonSerializer.Serialize(new PersonalRecallCalibrator.FeatureVector(.9, 0, .5, 0, 0, .5, 0, 1, 0));
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int index = 0; index < count; index++)
        {
            var reviewedAt = start.AddHours(offset + index);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO review_logs(card_id, reviewed_at, rating, state_before, engine_version, next_due, next_interval_days)
                VALUES($card, $reviewed, $rating, 1, 'seed', $next, 1);
                INSERT INTO model_predictions(review_log_id, profile_id, predicted_recall, created_at,
                                              raw_recall, model_version, features_json)
                VALUES(last_insert_rowid(), 1, .9, $reviewed, .9, 1, $features);
                """;
            command.Parameters.AddWithValue("$card", cardId);
            command.Parameters.AddWithValue("$reviewed", reviewedAt.ToString("O"));
            command.Parameters.AddWithValue("$next", reviewedAt.AddDays(1).ToString("O"));
            command.Parameters.AddWithValue("$rating", (offset + index) % 10 < 3 ? (int)Grade.Good : (int)Grade.Again);
            command.Parameters.AddWithValue("$features", features);
            await command.ExecuteNonQueryAsync();
        }
    }

    private async Task<long> CountProfilesAsync()
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "calibrator.db"),
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM model_profiles WHERE kind='personal-recall-calibrator'";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> CountReviewsAsync(long cardId)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "calibrator.db"),
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM review_logs WHERE card_id=$card";
        command.Parameters.AddWithValue("$card", cardId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
