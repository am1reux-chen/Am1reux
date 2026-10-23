using Lexico.Core.Data;
using Lexico.Core.Domain;
using Lexico.Core.Scheduling;
using Lexico.Core.Study;
using Xunit;

namespace Lexico.Core.Tests;

public sealed class UserDataBackupServiceTests : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lexio-user-backup-" + Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;
    private readonly string _automaticBackupPath;
    private readonly AppDatabase _db;
    private readonly UserDataBackupService _service;

    public UserDataBackupServiceTests()
    {
        _databasePath = Path.Combine(_directory, "lexio.db");
        _automaticBackupPath = _databasePath + ".backup";
        _db = new AppDatabase(_databasePath);
        _db.MigrateAsync().GetAwaiter().GetResult();
        _service = new UserDataBackupService(_db, _databasePath, _automaticBackupPath);
    }

    [Fact]
    public async Task FullBackupRestoresVocabularySettingsAndReviewHistoryAndRefreshesAutomaticBackup()
    {
        var deckId = await _db.EnsureDeckAsync("Imported deck", "User-owned vocabulary", "import", builtIn: false);
        var wordId = await _db.UpsertWordAsync(new(0, "backup-alpha", null, null, 1, "A1",
            [new(0, "n.", "备份前释义", null, "Example.", "例句。") ]));
        await _db.AddWordToDeckAsync(deckId, wordId, 0);
        await _db.EnsureCardsForDeckAsync(deckId);
        await _db.SetSettingAsync("desired_retention", "0.86");

        var study = new StudyService(_db, new FsrsCoreEngine(enableFuzzing: false));
        var plan = await study.StartAsync(Intensity.Normal, deckId);
        Assert.True(plan.TotalCards > 0);
        var item = Assert.IsType<StudyItem>(study.PeekNext());
        await study.AnswerAsync(item, Grade.Good);
        await study.FinishAsync();

        var backupPath = Path.Combine(_directory, "lexio-full.lexio-backup");
        await _service.CreateAsync(backupPath);
        await _db.SetSettingAsync("desired_retention", "0.72");
        await _db.UpsertWordAsync(new(0, "backup-beta", null, null, null, null, []));

        var result = await _service.RestoreAsync(backupPath);

        Assert.True(File.Exists(result.SafetyBackupPath));
        Assert.True(result.AutomaticBackupUpdated);
        Assert.Equal("0.86", await _db.GetSettingAsync("desired_retention"));
        Assert.Single(await _db.SearchWordsAsync("backup-alpha", 5));
        Assert.Empty(await _db.SearchWordsAsync("backup-beta", 5));
        Assert.Equal(1, await ExportedReviewCountAsync(_db, Path.Combine(_directory, "restored.csv")));

        await using var preRestore = await AppDatabase.OpenWithRecoveryAsync(result.SafetyBackupPath, result.SafetyBackupPath + ".fallback");
        Assert.Equal(2, await preRestore.CountWordsAsync());
        Assert.Equal("0.72", await preRestore.GetSettingAsync("desired_retention"));

        await using var autoBackup = await AppDatabase.OpenWithRecoveryAsync(_automaticBackupPath, _automaticBackupPath + ".fallback");
        Assert.Single(await autoBackup.SearchWordsAsync("backup-alpha", 5));
        Assert.Empty(await autoBackup.SearchWordsAsync("backup-beta", 5));
    }

    [Fact]
    public async Task FailedRestorePreservesCurrentDataAndLeavesSafetyBackup()
    {
        await _db.UpsertWordAsync(new(0, "preserve-me", null, null, null, null, []));
        var invalidBackup = Path.Combine(_directory, "not-a-backup.lexio-backup");
        await File.WriteAllTextAsync(invalidBackup, "not a SQLite database");

        var error = await Assert.ThrowsAsync<UserDataRestoreException>(() => _service.RestoreAsync(invalidBackup));

        Assert.True(File.Exists(error.SafetyBackupPath));
        Assert.True(error.CurrentDataRestored);
        Assert.Single(await _db.SearchWordsAsync("preserve-me", 5));
    }

    [Fact]
    public async Task AutomaticBackupRefreshFailureDoesNotUndoSuccessfulRestore()
    {
        var backupPath = Path.Combine(_directory, "before-restore.lexio-backup");
        await _db.UpsertWordAsync(new(0, "restored-from-backup", null, null, null, null, []));
        await _service.CreateAsync(backupPath);
        await _db.UpsertWordAsync(new(0, "present-before-restore", null, null, null, null, []));
        Directory.CreateDirectory(_automaticBackupPath);

        var result = await _service.RestoreAsync(backupPath);

        Assert.True(File.Exists(result.SafetyBackupPath));
        Assert.False(result.AutomaticBackupUpdated);
        Assert.NotNull(result.AutomaticBackupError);
        Assert.Single(await _db.SearchWordsAsync("restored-from-backup", 5));
        Assert.Empty(await _db.SearchWordsAsync("present-before-restore", 5));

        await using var preRestore = await AppDatabase.OpenWithRecoveryAsync(
            result.SafetyBackupPath, result.SafetyBackupPath + ".fallback");
        Assert.Single(await preRestore.SearchWordsAsync("present-before-restore", 5));
    }

    [Fact]
    public async Task ReloadAfterRestoreDropsActiveSessionThatReferencesCardsMissingFromRestoredDatabase()
    {
        var backupPath = Path.Combine(_directory, "before-session.lexio-backup");
        await _service.CreateAsync(backupPath);

        var deckId = await _db.EnsureDeckAsync("After backup", "Created after backup", "import", builtIn: false);
        var wordId = await _db.UpsertWordAsync(new(0, "post-backup-card", null, null, 1, "A1", []));
        await _db.AddWordToDeckAsync(deckId, wordId, 0);
        await _db.EnsureCardsForDeckAsync(deckId);

        var study = new StudyService(_db, new FsrsCoreEngine(enableFuzzing: false));
        var plan = await study.StartAsync(Intensity.Normal);
        Assert.True(plan.TotalCards > 0);
        var staleItem = Assert.IsType<StudyItem>(study.PeekNext());
        Assert.Equal("post-backup-card", staleItem.Lemma);

        await _service.RestoreAsync(backupPath);
        await study.ReloadAfterRestoreAsync(new FsrsCoreEngine(enableFuzzing: false));

        Assert.False(study.IsActive);
        Assert.Equal(0, study.Remaining);
        Assert.Null(study.PeekNext());
        Assert.Null(await study.AnswerAsync(staleItem, Grade.Good));
        Assert.Empty(await _db.SearchWordsAsync("post-backup-card", 5));
    }

    [Fact]
    public async Task ReloadAfterRestoreRehydratesTheSessionSavedInTheBackup()
    {
        var restoredDeckId = await _db.EnsureDeckAsync("Saved deck", "Present in backup", "import", builtIn: false);
        var restoredWordId = await _db.UpsertWordAsync(new(0, "restored-session-card", null, null, 1, "A1", []));
        await _db.AddWordToDeckAsync(restoredDeckId, restoredWordId, 0);
        await _db.EnsureCardsForDeckAsync(restoredDeckId);

        var study = new StudyService(_db, new FsrsCoreEngine(enableFuzzing: false));
        var savedPlan = await study.StartAsync(Intensity.Normal, restoredDeckId);
        Assert.True(savedPlan.TotalCards > 0);
        var savedItem = Assert.IsType<StudyItem>(study.PeekNext());
        var backupPath = Path.Combine(_directory, "active-session.lexio-backup");
        await _service.CreateAsync(backupPath);

        await study.FinishAsync();
        var newerDeckId = await _db.EnsureDeckAsync("Newer deck", "Absent from backup", "import", builtIn: false);
        var newerWordId = await _db.UpsertWordAsync(new(0, "post-backup-session-card", null, null, 2, "A1", []));
        await _db.AddWordToDeckAsync(newerDeckId, newerWordId, 0);
        await _db.EnsureCardsForDeckAsync(newerDeckId);
        var newerPlan = await study.StartAsync(Intensity.Normal, newerDeckId);
        Assert.True(newerPlan.TotalCards > 0);
        Assert.Equal("post-backup-session-card", study.PeekNext()!.Lemma);

        await _service.RestoreAsync(backupPath);
        await study.ReloadAfterRestoreAsync(new FsrsCoreEngine(enableFuzzing: false));

        Assert.True(study.IsActive);
        Assert.Equal(1, study.Remaining);
        Assert.Equal(savedItem.CardId, study.PeekNext()!.CardId);
        Assert.Empty(await _db.SearchWordsAsync("post-backup-session-card", 5));
    }

    private static async Task<int> ExportedReviewCountAsync(AppDatabase database, string path)
    {
        await database.ExportReviewHistoryCsvAsync(path);
        return (await File.ReadAllLinesAsync(path)).Length - 1;
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
