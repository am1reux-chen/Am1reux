using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lexio.Services;
using Lexico.Core.Data;
using Lexico.Core.Domain;
using Microsoft.UI.Xaml;

namespace Lexio.ViewModels;

public sealed record StudyDeckChoice(long? Id, string Name, int WordCount)
{
    public static StudyDeckChoice All { get; } = new(null, "全部词书", 0);
    public string DetailText => Id is null ? "按所有词书安排学习" : $"{WordCount:N0} 个词";
}

public sealed record StudyModeChoice(StudyMode Mode, string Name);

public sealed partial class IntensityOption : ObservableObject
{
    public Intensity Intensity { get; init; }
    public long? DeckId { get; init; }
    public int TotalCards { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string ReviewsText { get; init; }
    public required string NewText { get; init; }
    public required string MinutesText { get; init; }
    /// <summary>The page VM's start command; parameter is this option.</summary>
    public required System.Windows.Input.ICommand Start { get; init; }
}

public sealed partial class TodayViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Action<Exception>? _reportError;
    private string _loadError = "";
    private IntensityOption? _failedStartOption;
    private bool _noTasks;
    private StudyDeckChoice _selectedDeckChoice = StudyDeckChoice.All;

    [ObservableProperty] private string greeting = "";
    [ObservableProperty] private string dateText = "";
    [ObservableProperty] private string dueText = "";
    [ObservableProperty] private bool loading = true;
    [ObservableProperty] private bool canStart;
    [ObservableProperty] private StudyMode selectedStudyMode = StudyMode.Recall;

    public bool CanSelectDeck => !Loading;
    public bool CanSelectStudyMode => !Loading;
    public IReadOnlyList<StudyModeChoice> StudyModes { get; } =
    [
        new(StudyMode.Recall, "回忆"),
        new(StudyMode.FollowWrite, "跟写"),
        new(StudyMode.SpellFromMeaning, "看义拼写"),
        new(StudyMode.Dictation, "听写"),
        new(StudyMode.SelfTest, "自测"),
    ];

    public StudyDeckChoice SelectedDeckChoice
    {
        get => _selectedDeckChoice;
        private set => SetProperty(ref _selectedDeckChoice, value);
    }

    public bool NoTasks
    {
        get => _noTasks;
        private set => SetProperty(ref _noTasks, value);
    }

    public string LoadError
    {
        get => _loadError;
        private set => SetProperty(ref _loadError, value);
    }

    public ObservableCollection<IntensityOption> Options { get; } = [];
    public ObservableCollection<StudyDeckChoice> DeckChoices { get; } = [StudyDeckChoice.All];

    public IAsyncRelayCommand<IntensityOption> StartCommand { get; }
    public IAsyncRelayCommand RetryCommand { get; }

    public event Action? SessionStarted;

    partial void OnLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSelectDeck));
        OnPropertyChanged(nameof(CanSelectStudyMode));
    }

    public TodayViewModel(AppServices services, Action<Exception>? reportError = null)
    {
        _services = services;
        _reportError = reportError;
        StartCommand = new AsyncRelayCommand<IntensityOption>(StartAsync, option => CanStart && option is { TotalCards: > 0 });
        RetryCommand = new AsyncRelayCommand(RetryAsync);
    }

    public async Task LoadAsync()
    {
        _failedStartOption = null;
        var hadPreviousPlans = false;
        try
        {
            await _services.DispatchAsync(() =>
            {
                Loading = true;
                LoadError = "";
                var now = DateTime.Now;
                Greeting = now.Hour switch
                {
                    < 5 => "夜深了",
                    < 12 => "早上好",
                    < 18 => "下午好",
                    _ => "晚上好",
                };
                DateText = $"{now.Month}月{now.Day}日 {ChineseWeekdays.LongName(now.DayOfWeek)}";
                hadPreviousPlans = Options.Count > 0;
            }).ConfigureAwait(false);

            var nowUtc = DateTime.UtcNow;
            var decks = await _services.Db.ListDecksAsync().ConfigureAwait(false);
            var deckChoices = new List<StudyDeckChoice> { StudyDeckChoice.All };
            deckChoices.AddRange(decks.Select(deck => new StudyDeckChoice(deck.Id, deck.Name, deck.WordCount)));
            var selectedDeck = deckChoices.FirstOrDefault(choice => choice.Id == _services.SelectedStudyDeckId)
                ?? StudyDeckChoice.All;
            if (selectedDeck.Id != _services.SelectedStudyDeckId)
                await _services.SetSelectedStudyDeckIdAsync(selectedDeck.Id).ConfigureAwait(false);
            var due = await _services.Db.GetDueSnapshotAsync(nowUtc, selectedDeck.Id).ConfigureAwait(false);
            var plans = await _services.Study.BuildPlansAsync(nowUtc, selectedDeck.Id).ConfigureAwait(false);
            var options = new List<IntensityOption>();
            foreach (var intensity in new[] { Intensity.Light, Intensity.Normal, Intensity.Heavy })
            {
                var plan = plans[intensity];
                options.Add(new IntensityOption
                {
                    Intensity = intensity,
                    DeckId = selectedDeck.Id,
                    TotalCards = plan.ReviewCount + plan.EarlyReviewCount + plan.NewCount,
                    Title = intensity switch
                    {
                        Intensity.Light => "轻量",
                        Intensity.Normal => "常规",
                        Intensity.Heavy => "重度",
                        _ => "",
                    },
                    Description = intensity switch
                    {
                        Intensity.Light => "复习优先，同时学少量新词",
                        Intensity.Normal => "完成主要复习并学习新词",
                        Intensity.Heavy => "复习 + 提前复习 + 更多新词",
                        _ => "",
                    },
                    ReviewsText = plan.ReviewCount + plan.EarlyReviewCount > 0 || intensity != Intensity.Light
                        ? $"复习 {plan.ReviewCount + plan.EarlyReviewCount}"
                        : "复习 0",
                    NewText = $"新词 {plan.NewCount}",
                    MinutesText = plan.EstimatedMinutes > 0 ? $"约 {plan.EstimatedMinutes:0.#} 分钟" : "无任务",
                    Start = StartCommand,
                });
            }

            await _services.DispatchAsync(() =>
            {
                DeckChoices.Clear();
                foreach (var choice in deckChoices)
                    DeckChoices.Add(choice);
                SelectedDeckChoice = selectedDeck;
                var deckPrefix = selectedDeck.Id is null ? "全部词书" : selectedDeck.Name;
                DueText = $"{deckPrefix} · 今日到期复习 {due.DueReview + due.DueLearning} · 待学新词 {due.NewAvailable}";
                Options.Clear();
                foreach (var option in options)
                    Options.Add(option);
                CanStart = options.Any(option => option.TotalCards > 0);
                NoTasks = !CanStart;
                Loading = false;
                StartCommand.NotifyCanExecuteChanged();
            }).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _reportError?.Invoke(error);
            await _services.DispatchAsync(() =>
            {
                LoadError = hadPreviousPlans
                    ? "今日计划刷新失败，仍显示上次结果。请重试。"
                    : "今日计划加载失败，请重试。";
                Loading = false;
            }).ConfigureAwait(false);
        }
    }

    public async Task SelectDeckAsync(StudyDeckChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        if (Loading)
            return;

        if (SelectedDeckChoice.Id == choice.Id)
            return;

        var previousChoice = SelectedDeckChoice;
        SelectedDeckChoice = choice;
        try
        {
            await _services.SetSelectedStudyDeckIdAsync(choice.Id).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _reportError?.Invoke(error);
            await _services.DispatchAsync(() =>
            {
                SelectedDeckChoice = previousChoice;
                LoadError = "词书选择保存失败，请重试。";
            }).ConfigureAwait(false);
            return;
        }

        await _services.DispatchAsync(() =>
        {
            Options.Clear();
            DueText = "";
            CanStart = false;
            NoTasks = false;
            StartCommand.NotifyCanExecuteChanged();
        }).ConfigureAwait(false);
        await LoadAsync().ConfigureAwait(false);
    }

    private async Task StartAsync(IntensityOption? option)
    {
        if (option is not { TotalCards: > 0 } || !CanStart)
            return;
        _failedStartOption = null;
        LoadError = "";
        CanStart = false;
        Loading = true;
        var started = false;
        try
        {
            if (SelectedStudyMode == StudyMode.SelfTest)
            {
                var unavailableReason = await ValidateInputModeAsync(option).ConfigureAwait(false);
                if (unavailableReason is not null)
                {
                    await _services.DispatchAsync(() => LoadError = unavailableReason).ConfigureAwait(false);
                    return;
                }
            }

            await _services.Study.StartAsync(option.Intensity, SelectedStudyMode, option.DeckId).ConfigureAwait(false);
            started = true;
        }
        catch (Exception error)
        {
            _failedStartOption = option;
            _reportError?.Invoke(error);
            await _services.DispatchAsync(() => LoadError = "学习启动失败，请重试。").ConfigureAwait(false);
        }
        finally
        {
            await _services.DispatchAsync(() =>
            {
                CanStart = Options.Any(current => current.TotalCards > 0);
                Loading = false;
                StartCommand.NotifyCanExecuteChanged();
            }).ConfigureAwait(false);
        }

        if (started)
            await _services.DispatchAsync(() => SessionStarted?.Invoke()).ConfigureAwait(false);
    }

    private async Task<string?> ValidateInputModeAsync(IntensityOption option)
    {
        var plans = await _services.Study.BuildPlansAsync(DateTime.UtcNow, option.DeckId).ConfigureAwait(false);
        var cards = plans[option.Intensity].DueCards
            .Concat(plans[option.Intensity].EarlyReviewCards)
            .Concat(plans[option.Intensity].NewCards)
            .Select(candidate => candidate.Card)
            .DistinctBy(card => card.Id)
            .ToArray();
        var words = (await _services.Db.LoadWordsWithSenses(cards.Select(card => card.WordId).Distinct().ToArray())
            .ConfigureAwait(false)).ToDictionary(word => word.Id);
        var decks = (await _services.Db.ListDecksAsync().ConfigureAwait(false)).ToDictionary(deck => deck.Id);
        var wordsByDeck = new Dictionary<long, IReadOnlyList<WordEntry>>();

        foreach (var card in cards)
        {
            if (!words.TryGetValue(card.WordId, out var word) || FirstGloss(word.Senses) is not { } correctGloss)
                return "所选计划中有单词缺少释义，无法开始自测。请选择回忆、跟写、看义拼写或听写。";

            if (!decks.TryGetValue(card.DeckId, out var deck))
                return "自测词书信息无法读取，请重试或改用其他练习模式。";
            if (!wordsByDeck.TryGetValue(deck.Id, out var deckWords))
            {
                deckWords = await _services.Db.SearchWordsAsync("", Math.Max(1, deck.WordCount), deck.Id).ConfigureAwait(false);
                wordsByDeck[deck.Id] = deckWords;
            }

            int distractorCount = deckWords
                .Where(candidate => candidate.Id != word.Id)
                .Select(candidate => FirstGloss(candidate.Senses))
                .Where(gloss => !string.IsNullOrWhiteSpace(gloss) &&
                    !string.Equals(gloss, correctGloss, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .Count();
            if (distractorCount < 3)
                return $"“{word.Lemma}”所在词书没有三个不同的释义可作自测选项，请选择回忆、跟写或听写。";
        }

        return null;
    }

    private static string? FirstGloss(IReadOnlyList<WordSense> senses)
    {
        foreach (var sense in senses)
        {
            var parts = new[] { sense.ZhGloss, sense.EnGloss }.Where(value => !string.IsNullOrWhiteSpace(value));
            var gloss = string.Join(" · ", parts);
            if (gloss.Length > 0)
                return gloss;
        }
        return null;
    }

    private Task RetryAsync() => _failedStartOption is { } option ? StartAsync(option) : LoadAsync();
}
