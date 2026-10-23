using System.Collections.ObjectModel;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lexio.Services;
using Lexico.Core.Domain;

namespace Lexio.ViewModels;

public enum LibraryMarkFilter
{
    All,
    Favorite,
    Wrong,
    Mastered,
}

public sealed record LibraryMarkFilterChoice(LibraryMarkFilter Filter, string Name);

public sealed record SearchResultItem(Lexico.Core.Domain.WordEntry Word, string FirstLine)
{
    public IReadOnlyList<WordDetailSection> DetailSections => WordDetailSection.From(Word.Details);
    public string PronunciationDisplay => WordPronunciationDisplay.Format(Word.IpaUs, Word.IpaUk);
    public long CardId { get; init; }
    public long DeckId { get; init; }
    public string DeckName { get; init; } = "";
    public bool IsFavorite { get; init; }
    public bool IsWrong { get; init; }
    public bool IsMastered { get; init; }
}

public sealed record LibraryImportFeedback(string Title, string Message);

public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Action<Exception>? _reportError;
    private long _searchVersion;
    private string _decksLoadError = "";
    private DeckInfo? _selectedDeck;

    [ObservableProperty] private string query = "";
    [ObservableProperty] private bool searching;
    [ObservableProperty] private bool noResults;
    [ObservableProperty] private string statusText = "";
    [ObservableProperty] private string resultCountText = "";
    [ObservableProperty] private LibraryMarkFilter selectedMarkFilter = LibraryMarkFilter.All;
    public DeckInfo? SelectedDeck
    {
        get => _selectedDeck;
        private set
        {
            if (SetProperty(ref _selectedDeck, value))
                OnPropertyChanged(nameof(SearchScopeText));
        }
    }
    public LibraryImportFeedback? LastImportFeedback { get; private set; }
    public string SearchScopeText
    {
        get
        {
            string deck = SelectedDeck is null
                ? "搜索范围：全部词书"
                : $"搜索范围：{SelectedDeck.Name}（{SelectedDeck.WordCount:N0} 词）";
            return SelectedMarkFilter == LibraryMarkFilter.All
                ? deck
                : $"{deck} · {MarkFilterName(SelectedMarkFilter)}";
        }
    }
    public string DecksLoadError
    {
        get => _decksLoadError;
        private set => SetProperty(ref _decksLoadError, value);
    }

    public ObservableCollection<SearchResultItem> Results { get; } = [];
    public ObservableCollection<DeckInfo> Decks { get; } = [];
    public IReadOnlyList<LibraryMarkFilterChoice> MarkFilters { get; } =
    [
        new(LibraryMarkFilter.All, "全部单词"),
        new(LibraryMarkFilter.Favorite, "收藏"),
        new(LibraryMarkFilter.Wrong, "错词"),
        new(LibraryMarkFilter.Mastered, "已掌握"),
    ];

    public IAsyncRelayCommand<string> ImportCommand { get; }
    public IAsyncRelayCommand RetryDecksCommand { get; }

    partial void OnSelectedMarkFilterChanged(LibraryMarkFilter value)
    {
        OnPropertyChanged(nameof(SearchScopeText));
        _ = SearchAsync(Query);
    }

    public LibraryViewModel(AppServices services, Action<Exception>? reportError = null)
    {
        _services = services;
        _reportError = reportError;
        ImportCommand = new AsyncRelayCommand<string>(ImportAsync);
        RetryDecksCommand = new AsyncRelayCommand(LoadDecksForDisplayAsync);
    }

    public async Task LoadDecksForDisplayAsync()
    {
        try
        {
            await LoadDecksAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _reportError?.Invoke(error);
            await _services.DispatchAsync(() => DecksLoadError = "词书列表加载失败，请重试。").ConfigureAwait(false);
        }
    }

    public async Task LoadDecksAsync()
    {
        var decks = await _services.Db.ListDecksAsync().ConfigureAwait(false);
        await _services.DispatchAsync(() =>
        {
            Decks.Clear();
            foreach (var deck in decks)
                Decks.Add(deck);
            DecksLoadError = "";
        }).ConfigureAwait(false);
    }

    public async Task SearchAsync(string query)
    {
        Query = query;
        long searchVersion = Interlocked.Increment(ref _searchVersion);
        long? deckId = SelectedDeck?.Id;
        if (string.IsNullOrWhiteSpace(query) && deckId is null && SelectedMarkFilter == LibraryMarkFilter.All)
        {
            await _services.DispatchAsync(() =>
            {
                if (searchVersion != Volatile.Read(ref _searchVersion))
                    return;
                Results.Clear();
                NoResults = false;
                ResultCountText = "";
                Searching = false;
            }).ConfigureAwait(false);
            return;
        }

        Searching = true;
        List<SearchResultItem> items;
        try
        {
            if (SelectedMarkFilter == LibraryMarkFilter.All)
            {
                var results = await _services.Db.SearchLibraryCardsAsync(query, 50, deckId).ConfigureAwait(false);
                items = results.Select(entry => new SearchResultItem(entry.WordEntry, FirstSenseLine(entry.WordEntry.Senses))
                {
                    CardId = entry.CardId,
                    DeckId = entry.DeckId,
                    DeckName = entry.DeckName,
                    IsFavorite = entry.IsFavorite,
                    IsWrong = entry.IsWrong,
                    IsMastered = entry.IsMastered,
                }).ToList();
            }
            else
            {
                var results = await _services.Db.SearchMarkedWordsAsync(query, ToCardMark(SelectedMarkFilter), deckId, 50)
                    .ConfigureAwait(false);
                items = results.Select(entry => new SearchResultItem(entry.WordEntry, FirstSenseLine(entry.WordEntry.Senses))
                {
                    CardId = entry.CardId,
                    DeckId = entry.DeckId,
                    DeckName = entry.DeckName,
                    IsFavorite = entry.IsFavorite,
                    IsWrong = entry.IsWrong,
                    IsMastered = entry.IsMastered,
                }).ToList();
            }
        }
        catch (Exception error)
        {
            await _services.DispatchAsync(() =>
            {
                if (searchVersion != Volatile.Read(ref _searchVersion))
                    return;
                Results.Clear();
                NoResults = false;
                ResultCountText = "搜索失败，请重试。";
                Searching = false;
            }).ConfigureAwait(false);
            _reportError?.Invoke(error);
            return;
        }

        await _services.DispatchAsync(() =>
        {
            if (searchVersion != Volatile.Read(ref _searchVersion))
                return;
            Results.Clear();
            foreach (var item in items)
                Results.Add(item);
            NoResults = items.Count == 0;
            ResultCountText = items.Count == 0
                ? ""
                : string.IsNullOrWhiteSpace(query) && SelectedDeck is { } selectedDeck
                    ? $"显示 {items.Count} / {selectedDeck.WordCount:N0} 个词"
                    : $"{items.Count} 个结果";
            Searching = false;
        }).ConfigureAwait(false);
    }

    public async Task SelectDeckAsync(DeckInfo? deck)
    {
        SelectedDeck = deck;
        await SearchAsync(Query).ConfigureAwait(false);
    }

    public async Task SetMarkAsync(long cardId, CardMark mark, bool enabled)
    {
        try
        {
            await _services.Db.SetCardMarkAsync(cardId, mark, enabled).ConfigureAwait(false);
            await _services.DispatchAsync(() => StatusText = enabled ? "标记已保存。" : "标记已清除。")
                .ConfigureAwait(false);
            await SearchAsync(Query).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _reportError?.Invoke(error);
            await _services.DispatchAsync(() => StatusText = "标记保存失败，正在恢复原状态。请重试。")
                .ConfigureAwait(false);
            await SearchAsync(Query).ConfigureAwait(false);
        }
    }

    public static string MarkFilterName(LibraryMarkFilter filter) => filter switch
    {
        LibraryMarkFilter.Favorite => "收藏",
        LibraryMarkFilter.Wrong => "错词",
        LibraryMarkFilter.Mastered => "已掌握",
        _ => "全部单词",
    };

    private static CardMark ToCardMark(LibraryMarkFilter filter) => filter switch
    {
        LibraryMarkFilter.Favorite => CardMark.Favorite,
        LibraryMarkFilter.Wrong => CardMark.Wrong,
        LibraryMarkFilter.Mastered => CardMark.Mastered,
        _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, "All does not map to a card mark."),
    };

    internal static string FirstSenseLine(IReadOnlyList<WordSense> senses)
    {
        if (senses.Count == 0)
            return "";
        var s = senses[0];
        return string.Join("  ", new[] { s.Pos, s.ZhGloss, s.EnGloss }.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private async Task ImportAsync(string? path)
    {
        LastImportFeedback = null;
        if (string.IsNullOrWhiteSpace(path))
            return;
        await _services.DispatchAsync(() => StatusText = "导入中…").ConfigureAwait(false);
        var deckName = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(deckName))
            deckName = "导入词书";
        string extension = Path.GetExtension(path);
        var result = extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
            ? await _services.Dictionary.ImportTypeWordsArchiveAsync(path).ConfigureAwait(false)
            : extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
                ? await _services.Dictionary.ImportTypeWordsJsonAsync(path, deckName).ConfigureAwait(false)
                : await _services.Dictionary.ImportCsvAsync(path, deckName).ConfigureAwait(false);
        deckName = result.DeckName ?? deckName;
        string sourceCountNote = result.ExpectedCount is { } expected && result.SourceCount is { } actual && expected != actual
            ? $"（TypeWords 清单标记 {expected} 条，ZIP 实际 {actual} 条）"
            : "";
        if (result.Failure is not null)
            _reportError?.Invoke(result.Failure);
        await LoadDecksAsync().ConfigureAwait(false);
        if (result.Error is null)
        {
            Func<Task> refreshSearch = () => SearchAsync(Query);
            await _services.DispatchAsync(refreshSearch).ConfigureAwait(false);
        }

        await _services.DispatchAsync(() =>
        {
            var success = result.Error is null;
            StatusText = success
                ? $"导入完成：「{deckName}」新增 {result.Added}，更新 {result.Updated}，跳过 {result.Skipped}{sourceCountNote}"
                : $"导入失败：{result.Error}";
            LastImportFeedback = new LibraryImportFeedback(success ? "导入完成" : "导入失败", StatusText);
        }).ConfigureAwait(false);
    }
}
