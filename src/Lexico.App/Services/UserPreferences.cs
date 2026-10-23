using System.Globalization;
using System.Threading;
using Lexico.Core.Data;

namespace Lexio.Services;

internal sealed class UserPreferences
{
    private const string StudyDeckSetting = "selected_study_deck_id";

    private static readonly (string Key, bool Default)[] BooleanSettings =
    [
        ("reduce_motion", false),
        ("four_grade_mode", false),
        ("show_ipa", true),
        ("auto_pronounce", false),
    ];

    private readonly Lock _gate = new();
    private Dictionary<string, string> _values = [];

    public async Task LoadAsync(AppDatabase database)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, fallback) in BooleanSettings)
            values[key] = await database.GetSettingAsync(key).ConfigureAwait(false) ?? (fallback ? "1" : "0");
        values[StudyDeckSetting] = await database.GetSettingAsync(StudyDeckSetting).ConfigureAwait(false) ?? "";

        lock (_gate)
            _values = values;
    }

    public bool GetBool(string key, bool fallback)
    {
        lock (_gate)
            return _values.TryGetValue(key, out var value) ? value == "1" : fallback;
    }

    public void SetBool(string key, bool value)
    {
        lock (_gate)
            _values[key] = value ? "1" : "0";
    }

    public long? SelectedStudyDeckId
    {
        get
        {
            lock (_gate)
                return _values.TryGetValue(StudyDeckSetting, out var value) &&
                    long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                    ? id
                    : null;
        }
    }

    public async Task SetSelectedStudyDeckIdAsync(AppDatabase database, long? deckId)
    {
        var value = deckId?.ToString(CultureInfo.InvariantCulture) ?? "";
        await database.SetSettingAsync(StudyDeckSetting, value).ConfigureAwait(false);
        lock (_gate)
            _values[StudyDeckSetting] = value;
    }
}
