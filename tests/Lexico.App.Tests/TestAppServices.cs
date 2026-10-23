using Lexico.Core.Data;
using Lexico.Core.Dictionary;
using Lexico.Core.Study;

namespace Lexio.Services;

/// <summary>Test adapter for production view-model app-service boundaries.</summary>
public sealed class AppServices
{
    private readonly UserPreferences _preferences = new();

    public static string StartupLogPath => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lexio-app-tests.log");

    public StudyService Study { get; } = null!;
    public AppDatabase Db { get; } = null!;
    public DictionaryService Dictionary { get; } = null!;
    public ArticlePracticeService ArticlePractice => new(Db);
    public TestSpeech Speech { get; } = new();
    public bool FourGradeMode => false;
    public bool ShowIpa => true;
    public bool AutoPronounce => false;
    public long? SelectedStudyDeckId => _preferences.SelectedStudyDeckId;
    public Func<Action, Task>? ActionDispatcher { get; set; }
    public Func<long?, Task>? StudyDeckSave { get; set; }
    public Func<long?, Task>? BeforeStudyDeckSave { get; set; }

    public AppServices(StudyService study) => Study = study;

    public AppServices(AppDatabase db)
    {
        Db = db;
        Dictionary = new DictionaryService(db);
    }

    public AppServices(AppDatabase db, StudyService study)
    {
        Db = db;
        Study = study;
        Dictionary = new DictionaryService(db);
    }

    public Task DispatchAsync(Func<Task> work) => work();

    public Task DispatchAsync(Action action)
    {
        if (ActionDispatcher is not null)
            return ActionDispatcher(action);
        action();
        return Task.CompletedTask;
    }

    public Task LoadUserPreferencesAsync() => Db is null ? Task.CompletedTask : _preferences.LoadAsync(Db);

    public async Task SetSelectedStudyDeckIdAsync(long? deckId)
    {
        if (Db is null)
            throw new InvalidOperationException("Study-book selection requires a test database.");
        if (BeforeStudyDeckSave is not null)
            await BeforeStudyDeckSave(deckId);
        if (StudyDeckSave is not null)
        {
            await StudyDeckSave(deckId);
            return;
        }

        await _preferences.SetSelectedStudyDeckIdAsync(Db, deckId);
    }
}

public sealed class TestSpeech
{
    public Task SpeakAsync(string text) => Task.CompletedTask;
    public Task<bool> TrySpeakAsync(string text) => Task.FromResult(true);
}
