using Lexio.Services;
using Lexico.Core.Data;
using Xunit;

namespace Lexico.App.Tests;

public sealed class UserPreferencesTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lexio-user-preferences-" + Guid.NewGuid().ToString("N"));
    private AppDatabase _db = null!;

    public async Task InitializeAsync()
    {
        _db = new AppDatabase(Path.Combine(_directory, "preferences.db"));
        await _db.MigrateAsync();
    }

    [Fact]
    public async Task LoadCachesPersistedPreferencesAndDefaultsForSynchronousAccess()
    {
        await _db.SetSettingAsync("auto_pronounce", "1");
        await _db.SetSettingAsync("four_grade_mode", "0");
        var preferences = new UserPreferences();

        await preferences.LoadAsync(_db);

        Assert.True(preferences.GetBool("auto_pronounce", false));
        Assert.False(preferences.GetBool("four_grade_mode", false));
        Assert.False(preferences.GetBool("reduce_motion", false));
        Assert.True(preferences.GetBool("show_ipa", true));
    }

    [Fact]
    public async Task ReloadReplacesCachedPreferencesAfterDatabaseChanges()
    {
        await _db.SetSettingAsync("auto_pronounce", "1");
        var preferences = new UserPreferences();
        await preferences.LoadAsync(_db);
        Assert.True(preferences.GetBool("auto_pronounce", false));

        await _db.SetSettingAsync("auto_pronounce", "0");
        await preferences.LoadAsync(_db);

        Assert.False(preferences.GetBool("auto_pronounce", false));
    }

    [Fact]
    public async Task SelectedStudyBookPersistsAcrossPreferenceReloads()
    {
        var preferences = new UserPreferences();
        await preferences.LoadAsync(_db);
        await preferences.SetSelectedStudyDeckIdAsync(_db, 42);

        var restartedPreferences = new UserPreferences();
        await restartedPreferences.LoadAsync(_db);
        Assert.Equal(42, restartedPreferences.SelectedStudyDeckId);

        await restartedPreferences.SetSelectedStudyDeckIdAsync(_db, null);
        var allBooksPreferences = new UserPreferences();
        await allBooksPreferences.LoadAsync(_db);
        Assert.Null(allBooksPreferences.SelectedStudyDeckId);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
