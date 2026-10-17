using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lexio.Services;
using Lexico.Core.Domain;

namespace Lexio.ViewModels;

public sealed record SearchResultItem(Lexico.Core.Domain.WordEntry Word, string FirstLine);

public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly AppServices _services;

    [ObservableProperty] private string query = "";
    [ObservableProperty] private bool searching;
    [ObservableProperty] private bool noResults;
    [ObservableProperty] private string statusText = "";
    [ObservableProperty] private string resultCountText = "";

    public ObservableCollection<SearchResultItem> Results { get; } = [];
    public ObservableCollection<DeckInfo> Decks { get; } = [];

    public IAsyncRelayCommand<string> ImportCommand { get; }

    public LibraryViewModel(AppServices services)
    {
        _services = services;
        ImportCommand = new AsyncRelayCommand<string>(ImportAsync);
    }

    public async Task LoadDecksAsync()
    {
        var decks = await _services.Db.ListDecksAsync().ConfigureAwait(false);
        await _services.DispatchAsync(() =>
        {
            Decks.Clear();
            foreach (var deck in decks)
                Decks.Add(deck);
        }).ConfigureAwait(false);
    }

    public async Task SearchAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            await _services.DispatchAsync(() =>
            {
                Results.Clear();
                NoResults = false;
                ResultCountText = "";
            }).ConfigureAwait(false);
            return;
        }

        Searching = true;
        var results = await _services.Db.SearchWordsAsync(query, 50).ConfigureAwait(false);
        var items = results.Select(w => new SearchResultItem(w, FirstSenseLine(w.Senses))).ToList();
        await _services.DispatchAsync(() =>
        {
            Results.Clear();
            foreach (var item in items)
                Results.Add(item);
            NoResults = items.Count == 0;
            ResultCountText = items.Count > 0 ? $"{items.Count} 个结果" : "";
            Searching = false;
        }).ConfigureAwait(false);
    }

    internal static string FirstSenseLine(IReadOnlyList<WordSense> senses)
    {
        if (senses.Count == 0)
            return "";
        var s = senses[0];
        return string.Join("  ", new[] { s.Pos, s.ZhGloss, s.EnGloss }.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private async Task ImportAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        StatusText = "导入中…";
        var deckName = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(deckName))
            deckName = "导入词书";
        var result = await _services.Dictionary.ImportCsvAsync(path, deckName).ConfigureAwait(false);
        await _services.DispatchAsync(() =>
        {
            StatusText = result.Error is not null
                ? $"导入失败：{result.Error}"
                : $"已导入 {result.Added} 个词到「{deckName}」（跳过 {result.Skipped}）";
        }).ConfigureAwait(false);
        await LoadDecksAsync().ConfigureAwait(false);
    }
}
