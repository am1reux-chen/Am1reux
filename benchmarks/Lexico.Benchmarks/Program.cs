using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Lexico.Core.Data;
using Lexico.Core.Domain;
using Lexico.Core.Scheduling;
using Lexico.Core.Study;
using Microsoft.Data.Sqlite;

var wordCount = args.Length > 0 && int.TryParse(args[0], out var parsedWords) ? Math.Clamp(parsedWords, 100, 50_000) : 10_000;
var sampleCount = args.Length > 1 && int.TryParse(args[1], out var parsedSamples) ? Math.Clamp(parsedSamples, 3, 15) : 7;
var root = Path.Combine(Path.GetTempPath(), "lexio-benchmark-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

try
{
    var databasePath = Path.Combine(root, "benchmark.db");
    await using var db = new AppDatabase(databasePath);
    await db.MigrateAsync();

    var entries = Enumerable.Range(0, wordCount)
        .Select(index => new WordEntry(0, $"lexiobench{index:D6}", null, null, index + 1, "B1", []))
        .ToArray();
    var importTimer = Stopwatch.StartNew();
    var imported = await db.ImportWordsAsync(entries, "Benchmark", "lexio-performance-benchmark");
    importTimer.Stop();

    var deck = (await db.ListDecksAsync()).Single(value => value.Name == "Benchmark");
    var cards = await db.GetNewCardsAsync(wordCount, deck.Id);
    if (cards.Count != wordCount || imported.Added != wordCount)
        throw new InvalidOperationException($"Fixture expected {wordCount} cards; imported {imported.Added}, loaded {cards.Count}.");

    var engine = new FsrsCoreEngine(enableFuzzing: false);
    var now = DateTime.UtcNow;
    var schedulingCard = new SrsCardSnapshot(CardState.Review, null, 8, 5,
        now.AddDays(-1), now.AddDays(-8));
    var scheduling = Measure("fsrs_schedule", sampleCount, 10_000,
        () => _ = engine.Schedule(schedulingCard, Grade.Good, now, elapsedMs: 900));

    var targetWordId = cards[^1].WordId;
    var reads = MeasureAsync("sqlite_word_read", sampleCount, 100,
        async () => _ = await db.GetWordAsync(targetWordId));
    var searches = MeasureAsync("fts5_prefix_search", sampleCount, 30,
        async () => _ = await db.SearchWordsAsync($"lexiobench{wordCount - 1:D6}"));

    var planCandidates = Enumerable.Range(0, wordCount)
        .Select(index => new DailyPlanner.Candidate(
            new CardRecord(index + 1, index + 1, deck.Id, CardState.Review, null, 8, 5,
                now.AddDays(-2), now.AddDays(-7), 5, index % 4, now.AddDays(-100)),
            index + 1,
            0.9))
        .ToArray();
    var baselines = new DailyPlanner.Baselines(60, 8, 4);
    var planning = Measure("daily_planner_10k", sampleCount, 1,
        () => _ = DailyPlanner.Build(Intensity.Heavy, planCandidates, baselines, now));

    await db.SetSettingAsync("baseline_daily_reviews", wordCount.ToString(CultureInfo.InvariantCulture));
    await SetAllCardsDueForReviewAsync(databasePath, now);
    var study = new StudyService(db, engine);
    var plannedSession = await study.StartAsync(Intensity.Heavy);
    if (plannedSession.TotalCards != wordCount)
        throw new InvalidOperationException($"Continuous session expected {wordCount} cards; planner selected {plannedSession.TotalCards}.");

    var answerDurationsMs = new double[wordCount];
    var answerCount = 0;
    var sessionTimer = Stopwatch.StartNew();
    while (study.PeekNext() is { } item)
    {
        var timer = Stopwatch.StartNew();
        await study.AnswerAsync(item, Grade.Good);
        timer.Stop();
        answerDurationsMs[answerCount++] = timer.Elapsed.TotalMilliseconds;
    }
    var summary = await study.FinishAsync();
    sessionTimer.Stop();
    if (answerCount != wordCount || summary.Reviews != wordCount)
        throw new InvalidOperationException($"Continuous session committed {summary.Reviews} reviews after presenting {answerCount} cards.");

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    using var process = Process.GetCurrentProcess();
    var output = new
    {
        machine = Environment.MachineName,
        os = RuntimeInformation.OSDescription,
        architecture = RuntimeInformation.OSArchitecture.ToString(),
        processorCount = Environment.ProcessorCount,
        runtime = RuntimeInformation.FrameworkDescription,
        datasetWords = wordCount,
        samples = sampleCount,
        importedWords = imported.Added,
        importElapsedMs = Math.Round(importTimer.Elapsed.TotalMilliseconds, 2),
        results = new object[]
        {
            scheduling,
            reads,
            searches,
            planning,
            new Measurement("continuous_study_session", answerCount,
                Math.Round(Percentile(answerDurationsMs, 0.50), 4),
                Math.Round(Percentile(answerDurationsMs, 0.95), 4)),
        },
        continuousSessionElapsedMs = Math.Round(sessionTimer.Elapsed.TotalMilliseconds, 2),
        continuousSessionReviewsCommitted = summary.Reviews,
        managedHeapBytesAfterFullGc = GC.GetTotalMemory(forceFullCollection: true),
        processPeakWorkingSetBytes = process.PeakWorkingSet64,
    };
    Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    }));
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch (IOException) { }
}

static async Task SetAllCardsDueForReviewAsync(string databasePath, DateTime nowUtc)
{
    await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Pooling = false,
    }.ToString());
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = """
        UPDATE cards
        SET state=1, step=NULL, stability=8, difficulty=5,
            due=$due, last_review=$reviewed, reps=5, lapses=0
        """;
    command.Parameters.AddWithValue("$due", nowUtc.AddDays(-2).ToString("O", CultureInfo.InvariantCulture));
    command.Parameters.AddWithValue("$reviewed", nowUtc.AddDays(-8).ToString("O", CultureInfo.InvariantCulture));
    await command.ExecuteNonQueryAsync();
}

static Measurement Measure(string name, int sampleCount, int operationsPerSample, Action operation)
{
    for (int index = 0; index < Math.Min(operationsPerSample, 100); index++)
        operation();
    var samples = new double[sampleCount];
    for (int sample = 0; sample < sampleCount; sample++)
    {
        var timer = Stopwatch.StartNew();
        for (int operationIndex = 0; operationIndex < operationsPerSample; operationIndex++)
            operation();
        timer.Stop();
        samples[sample] = timer.Elapsed.TotalMilliseconds / operationsPerSample;
    }
    return new Measurement(name, operationsPerSample, Math.Round(Percentile(samples, 0.50), 4), Math.Round(Percentile(samples, 0.95), 4));
}

static Measurement MeasureAsync(string name, int sampleCount, int operationsPerSample, Func<Task> operation)
{
    return Measure(name, sampleCount, operationsPerSample,
        () => operation().GetAwaiter().GetResult());
}

static double Percentile(IReadOnlyList<double> values, double percentile)
{
    var sorted = values.Order().ToArray();
    var index = Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1);
    return sorted[index];
}

sealed record Measurement(string Name, int OperationsPerSample, double MedianMsPerOperation, double P95MsPerOperation);
