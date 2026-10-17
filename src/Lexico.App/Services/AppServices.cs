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
    public Speech Speech { get; } = new();

    /// <summary>Becomes true once first-run seeding has settled. Pages gate their loads on this.</summary>
    public bool Ready { get; private set; }

    public event Action? ReadyChanged;

    private readonly DispatcherQueue _dispatcher;

    private AppServices(AppDatabase db, DictionaryService dictionary, StudyService study, DispatcherQueue dispatcher)
    {
        Db = db;
        Dictionary = dictionary;
        Study = study;
        _dispatcher = dispatcher;
    }

    public static void Initialize()
    {
        if (Instance is not null)
            return;
        var dispatcher = DispatcherQueue.GetForCurrentThread();

        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lexio");
        var db = new AppDatabase(Path.Combine(dir, "lexio.db"));
        db.MigrateAsync().GetAwaiter().GetResult(); // DDL only, milliseconds; DB must exist before any page loads

        var retention = db.GetSettingDoubleAsync("desired_retention", 0.9).GetAwaiter().GetResult();
        var engine = new FsrsCoreEngine(desiredRetention: retention);
        var services = new AppServices(db, new DictionaryService(db), new StudyService(db, engine), dispatcher);
        Instance = services;

        _ = Task.Run(async () =>
        {
            try
            {
                await services.Dictionary.EnsureStarterDeckAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Lexio] starter seed failed: {ex.Message}");
            }
            services.Ready = true;
            services._dispatcher.TryEnqueue(() => services.ReadyChanged?.Invoke());
        });
    }

    // ---- settings (async read-through cache) ----

    /// <summary>Run <paramref name="action"/> on the UI thread; completes when it has run.</summary>
    public Task DispatchAsync(Action action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(() =>
            {
                try { action(); tcs.SetResult(); }
                catch (Exception ex) { tcs.SetException(ex); }
            }))
        {
            try { action(); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        }
        return tcs.Task;
    }

    /// <summary>Async overload: the inner task runs on the UI thread and is awaited.</summary>
    public Task DispatchAsync(Func<Task> work)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Run()
        {
            try
            {
                work().ContinueWith(t =>
                {
                    if (t.IsFaulted) tcs.TrySetException(t.Exception!.GetBaseException());
                    else tcs.TrySetResult();
                }, TaskScheduler.Default);
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }
        if (!_dispatcher.TryEnqueue(Run))
            Run();
        return tcs.Task;
    }

    public bool ReduceMotion => GetBool("reduce_motion", false);
    public bool FourGradeMode => GetBool("four_grade_mode", false);
    public bool ShowIpa => GetBool("show_ipa", true);
    public bool AutoPronounce => GetBool("auto_pronounce", false);

    private readonly Dictionary<string, string> _settingCache = [];

    private bool GetBool(string key, bool fallback)
    {
        if (_settingCache.TryGetValue(key, out var raw))
            return raw == "1";
        var value = Db.GetSettingAsync(key).GetAwaiter().GetResult();
        if (value is null)
            return fallback;
        _settingCache[key] = value;
        return value == "1";
    }

    public async Task SetBoolAsync(string key, bool value)
    {
        _settingCache[key] = value ? "1" : "0";
        await Db.SetSettingAsync(key, value ? "1" : "0").ConfigureAwait(false);
    }
}
