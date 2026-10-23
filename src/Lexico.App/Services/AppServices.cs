using Lexico.Core.Data;
using Lexico.Core.Dictionary;
using Lexico.Core.Scheduling;
using Lexico.Core.Study;
using Microsoft.UI.Dispatching;

namespace Lexio.Services;

/// <summary>Hand-rolled composition root. One instance per process; no DI container needed at this size.</summary>
public sealed class AppServices
{
    public static AppServices Instance { get; private set; } = null!;

    public AppDatabase Db { get; }
    public DictionaryService Dictionary { get; }
    public StudyService Study { get; }
    public ArticlePracticeService ArticlePractice { get; }
    public UserDataBackupService DataBackup { get; }
    public Speech Speech { get; } = new(error => StartupFailureReporter.WriteTechnicalError(
        error, StartupLogPath, "text-to-speech failed"));

    public static string DataDirectoryPath
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable("LEXIO_DATA_DIR");
            return string.IsNullOrWhiteSpace(overridePath)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lexio")
                : Path.GetFullPath(overridePath);
        }
    }

    public static string DatabasePath => Path.Combine(DataDirectoryPath, "lexio.db");
    public static string AutomaticBackupPath => DatabasePath + ".backup";
    public static string StartupLogPath => Path.Combine(DataDirectoryPath, "logs", "lexio.log");

    /// <summary>Becomes true once first-run seeding has settled. Pages gate their loads on this.</summary>
    public bool Ready { get; private set; }

    /// <summary>Current Today study-book selection, restored from settings when the app starts.</summary>
    public long? SelectedStudyDeckId => _userPreferences.SelectedStudyDeckId;

    public event Action? ReadyChanged;

    private readonly DispatcherQueue _dispatcher;
    private readonly Lock _shutdownLock = new();
    private readonly AsyncOperationGate _dataOperations = new();
    private AutomaticBackupWorker? _automaticBackupWorker;
    private Task? _shutdownTask;

    private AppServices(AppDatabase db, DictionaryService dictionary, StudyService study, DispatcherQueue dispatcher,
        string databasePath, string backupPath)
    {
        Db = db;
        Dictionary = dictionary;
        Study = study;
        ArticlePractice = new ArticlePracticeService(db);
        DataBackup = new UserDataBackupService(db, databasePath, backupPath);
        _dispatcher = dispatcher;
    }

    public static async Task InitializeAsync(string? recoveryBackupPath = null)
    {
        if (Instance is not null)
            return;
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        var databasePath = DatabasePath;
        var backupPath = AutomaticBackupPath;
        AppDatabase? db = null;
        AppServices services;
        try
        {
            db = await AppDatabase.OpenWithRecoveryAsync(databasePath, recoveryBackupPath ?? backupPath).ConfigureAwait(true);
            var retention = await db.GetSettingDoubleAsync("desired_retention", 0.9).ConfigureAwait(true);
            var engine = new FsrsCoreEngine(desiredRetention: retention);
            services = new AppServices(db, new DictionaryService(db), new StudyService(db, engine), dispatcher,
                databasePath, backupPath);
            await services.Study.RecoverInterruptedSessionsAsync().ConfigureAwait(true);
            await services.Dictionary.EnsureStarterDeckAsync().ConfigureAwait(true);
            await services.Dictionary.EnsureBuiltInVocabularyAsync().ConfigureAwait(true);
            await services._userPreferences.LoadAsync(db).ConfigureAwait(true);
        }
        catch
        {
            if (db is not null)
                await db.DisposeAsync().ConfigureAwait(true);
            throw;
        }

        Instance = services;
        services.Ready = true;
        services._dispatcher.TryEnqueue(() => services.ReadyChanged?.Invoke());
        services._automaticBackupWorker = new AutomaticBackupWorker(db!, backupPath, TimeSpan.FromMinutes(15),
            error => StartupFailureReporter.WriteTechnicalError(error, StartupLogPath, "automatic backup failed"));
    }

    public Task ShutdownAsync()
    {
        lock (_shutdownLock)
            return _shutdownTask ??= ShutdownCoreAsync();
    }

    private async Task ShutdownCoreAsync()
    {
        try { Speech.Dispose(); }
        catch (Exception error) { StartupFailureReporter.WriteTechnicalError(error, StartupLogPath, "disposing speech service failed"); }

        if (_automaticBackupWorker is not null)
        {
            try { await _automaticBackupWorker.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { StartupFailureReporter.WriteTechnicalError(error, StartupLogPath, "stopping automatic backups failed"); }
        }

        try { await _dataOperations.StopAndWaitAsync().ConfigureAwait(false); }
        catch (Exception error) { StartupFailureReporter.WriteTechnicalError(error, StartupLogPath, "waiting for data operations to finish failed"); }

        try { await Study.WaitForPendingOperationsAsync().ConfigureAwait(false); }
        catch (Exception error) { StartupFailureReporter.WriteTechnicalError(error, StartupLogPath, "waiting for active study operations failed"); }

        try { await Db.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { StartupFailureReporter.WriteTechnicalError(error, StartupLogPath, "disposing application database failed"); }
    }

    public Task RunDataOperationAsync(Func<Task> operation) => _dataOperations.RunAsync(operation);

    public Task<UserDataRestoreResult> RestoreUserDataAsync(string sourcePath) =>
        _dataOperations.RunAsync(() => RestoreUserDataCoreAsync(sourcePath));

    private async Task<UserDataRestoreResult> RestoreUserDataCoreAsync(string sourcePath)
    {
        await Study.WaitForPendingOperationsAsync().ConfigureAwait(false);
        bool resumeOnFailure = Study.IsActive && !Study.IsPaused;
        if (Study.IsActive)
            await Study.PauseAsync().ConfigureAwait(false);
        await Study.WaitForPendingOperationsAsync().ConfigureAwait(false);

        UserDataRestoreResult result;
        try
        {
            result = await DataBackup.RestoreAsync(sourcePath).ConfigureAwait(false);
        }
        catch (UserDataRestoreException error)
        {
            if (resumeOnFailure && error.CurrentDataRestored)
                await Study.ResumeAsync().ConfigureAwait(false);
            throw;
        }
        catch
        {
            if (resumeOnFailure)
                await Study.ResumeAsync().ConfigureAwait(false);
            throw;
        }

        try
        {
            await _userPreferences.LoadAsync(Db).ConfigureAwait(false);
            var retention = await Db.GetSettingDoubleAsync("desired_retention", 0.9).ConfigureAwait(false);
            await Study.ReloadAfterRestoreAsync(new FsrsCoreEngine(desiredRetention: retention)).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            throw new UserDataRestoreRefreshException(result, error);
        }

        return result;
    }

    // ---- cached user preferences ----

    /// <summary>Run <paramref name="action"/> on the UI thread; drop it if the queue is shutting down.</summary>
    public Task DispatchAsync(Action action)
    {
        return UiDispatcher.DispatchAsync(_dispatcher.HasThreadAccess,
            callback => _dispatcher.TryEnqueue(() => callback()), action);
    }

    /// <summary>Async overload: start the inner task on the UI thread, await it, and drop it during shutdown.</summary>
    public Task DispatchAsync(Func<Task> work)
    {
        return UiDispatcher.DispatchAsync(_dispatcher.HasThreadAccess,
            callback => _dispatcher.TryEnqueue(() => callback()), work);
    }

    public bool ReduceMotion => GetBool("reduce_motion", false);
    public bool FourGradeMode => GetBool("four_grade_mode", false);
    public bool ShowIpa => GetBool("show_ipa", true);
    public bool AutoPronounce => GetBool("auto_pronounce", false);

    private readonly UserPreferences _userPreferences = new();

    private bool GetBool(string key, bool fallback) => _userPreferences.GetBool(key, fallback);

    public async Task SetBoolAsync(string key, bool value)
    {
        await Db.SetSettingAsync(key, value ? "1" : "0").ConfigureAwait(false);
        _userPreferences.SetBool(key, value);
    }

    public Task SetSelectedStudyDeckIdAsync(long? deckId) =>
        _userPreferences.SetSelectedStudyDeckIdAsync(Db, deckId);
}

public sealed class UserDataRestoreRefreshException(UserDataRestoreResult restoreResult, Exception innerException)
    : Exception("数据已恢复，但应用无法同步当前学习状态；请重启 Lexio 后继续。", innerException)
{
    public UserDataRestoreResult RestoreResult { get; } = restoreResult;
}
