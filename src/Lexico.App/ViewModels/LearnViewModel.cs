using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lexio.Services;
using Lexico.Core.Domain;
using Lexico.Core.Study;

namespace Lexio.ViewModels;

public sealed record SenseLine(string Gloss, string? Example)
{
    public bool HasExample => !string.IsNullOrWhiteSpace(Example);
}

public sealed record WordDetailLine(string Primary, string? Secondary)
{
    public bool HasSecondary => !string.IsNullOrWhiteSpace(Secondary);
}

public sealed record WordDetailSection(string Title, IReadOnlyList<WordDetailLine> Lines)
{
    public static IReadOnlyList<WordDetailSection> From(WordDetails details)
    {
        var sections = new List<WordDetailSection>();
        Add("例句", details.Sentences.Select(text => Line(text.English, text.Chinese)));
        Add("短语", details.Phrases.Select(text => Line(text.English, text.Chinese)));
        Add("近义词", details.Synonyms.Select(synonym => Line(
            string.Join(" ", new[] { synonym.Pos, synonym.Gloss }.Where(value => !string.IsNullOrWhiteSpace(value))),
            string.Join(" · ", synonym.Words))));
        Add("词源", details.Etymologies.Select(etymology => Line(
            string.IsNullOrWhiteSpace(etymology.Title) ? etymology.Description : etymology.Title,
            string.IsNullOrWhiteSpace(etymology.Title) ? null : etymology.Description)));

        var related = new List<WordDetailLine>();
        if (!string.IsNullOrWhiteSpace(details.RelatedRoot))
            related.Add(new WordDetailLine($"词根 · {details.RelatedRoot}", null));
        foreach (var group in details.RelatedGroups)
        {
            related.AddRange(group.Words.Select(word =>
            {
                string gloss = string.Join(" ", new[] { group.Pos, word.Chinese }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
                return Line(word.English, gloss);
            }));
        }
        Add("相关词", related);
        return sections;

        static WordDetailLine Line(string? primary, string? secondary) => string.IsNullOrWhiteSpace(primary)
            ? new WordDetailLine(secondary ?? "", null)
            : new WordDetailLine(primary, string.IsNullOrWhiteSpace(secondary) ? null : secondary);

        void Add(string title, IEnumerable<WordDetailLine> lines)
        {
            var items = lines.Where(line => !string.IsNullOrWhiteSpace(line.Primary) || line.HasSecondary).ToArray();
            if (items.Length > 0)
                sections.Add(new WordDetailSection(title, items));
        }
    }
}

public sealed record SelfTestChoice(string Text, bool IsCorrect);

public sealed partial class LearnViewModel : ObservableObject
{
    private readonly AppServices _services;
    private int _totalPlanned;
    private bool _isPaused;
    private bool _hasMeaning;
    private int _completedCount;
    private int _remainingCount;
    private string _ipaUk = "";

    [ObservableProperty] private bool inSession;
    [ObservableProperty] private bool revealed;
    [ObservableProperty] private bool done;
    [ObservableProperty] private bool waiting;
    [ObservableProperty] private string lemma = "";
    [ObservableProperty] private string ipa = "";
    [ObservableProperty] private bool showIpa = true;
    [ObservableProperty] private string progressText = "";
    [ObservableProperty] private string waitText = "";
    [ObservableProperty] private string summaryText = "";
    [ObservableProperty] private string summaryDetail = "";
    [ObservableProperty] private string newBadge = "";
    [ObservableProperty] private StudyItem? current;
    [ObservableProperty] private bool fourGradeMode;
    [ObservableProperty] private StudyMode mode = StudyMode.Recall;
    [ObservableProperty] private string inputText = "";
    [ObservableProperty] private string inputFeedback = "";
    [ObservableProperty] private bool inputCorrect;
    [ObservableProperty] private bool answerSubmitted;
    [ObservableProperty] private bool wrongAnswerShown;
    [ObservableProperty] private bool selfTestAvailable;
    [ObservableProperty] private bool selfTestAnswered;
    [ObservableProperty] private string selfTestFeedback = "";
    [ObservableProperty] private string meaningPrompt = "";
    [ObservableProperty] private bool inputFocused;
    [ObservableProperty] private string speechError = "";
    [ObservableProperty] private bool isFavorite;
    [ObservableProperty] private bool isMastered;
    [ObservableProperty] private string markFeedback = "";

    public string KeyboardHint => Mode switch
    {
        StudyMode.FollowWrite => "输入上方单词并提交；答对后选择评分 · E 发音 · P 暂停 · Esc 结束",
        StudyMode.SpellFromMeaning => "根据释义输入单词；答对后选择评分 · P 暂停 · Esc 结束",
        StudyMode.Dictation => "听发音并输入单词 · R 重播 · 答对后选择评分 · P 暂停 · Esc 结束",
        StudyMode.SelfTest => "选择释义后自动评分 · P 暂停 · Esc 结束",
        _ => Revealed && !HasMeaning
            ? "释义缺失，不能评分 · Esc 结束本次学习"
            : FourGradeMode
                ? "Space 显示释义 · 1–4 评分 · E 发音 · P 暂停 · Esc 结束"
                : "Space 显示释义 · 1/3 评分 · E 发音 · P 暂停 · Esc 结束",
    };
    public string IpaUk
    {
        get => _ipaUk;
        private set
        {
            if (SetProperty(ref _ipaUk, value))
                OnPropertyChanged(nameof(IpaDisplay));
        }
    }
    public string IpaDisplay => WordPronunciationDisplay.Format(Ipa, IpaUk);
    public string PauseButtonAccessibleName => IsPaused ? "继续学习" : "暂停";
    public string InputPrompt => Mode switch
    {
        StudyMode.FollowWrite => "照着上方单词输入拼写",
        StudyMode.SpellFromMeaning => "根据释义拼出对应单词",
        StudyMode.Dictation => "听发音后输入单词",
        _ => "",
    };

    public ObservableCollection<SenseLine> Senses { get; } = [];
    public ObservableCollection<WordDetailSection> DetailSections { get; } = [];
    public ObservableCollection<SelfTestChoice> SelfTestChoices { get; } = [];

    public IRelayCommand RevealCommand { get; }
    public IAsyncRelayCommand PauseCommand { get; }
    public IAsyncRelayCommand ResumeCommand { get; }
    public IAsyncRelayCommand<string> GradeCommand { get; }
    public IAsyncRelayCommand EndCommand { get; }
    public IAsyncRelayCommand SpeakCommand { get; }
    public IAsyncRelayCommand SubmitInputCommand { get; }
    public IAsyncRelayCommand ContinueCommand { get; }
    public IAsyncRelayCommand DictationReplayCommand { get; }

    public bool IsPaused
    {
        get => _isPaused;
        private set
        {
            if (!SetProperty(ref _isPaused, value))
                return;
            OnPropertyChanged(nameof(ShowPaused));
            OnPropertyChanged(nameof(PauseButtonAccessibleName));
            NotifyPracticePresentationChanged();
            OnCanExecuteChanged();
        }
    }

    public int CompletedCount
    {
        get => _completedCount;
        private set => SetProperty(ref _completedCount, value);
    }

    public int RemainingCount
    {
        get => _remainingCount;
        private set => SetProperty(ref _remainingCount, value);
    }

    public bool HasMeaning
    {
        get => _hasMeaning;
        private set
        {
            if (SetProperty(ref _hasMeaning, value))
                NotifyMeaningPresentationChanged();
        }
    }

    /// <summary>Raised whenever a new card is shown (the page runs its entrance animation).</summary>
    public event Action? CardChanged;

    /// <summary>Raised when the session ends (the page shows the summary).</summary>
    public event Action? SessionFinished;

    public LearnViewModel(AppServices services)
    {
        _services = services;
        ShowIpa = services.ShowIpa;
        RevealCommand = new RelayCommand(Reveal, () => InSession && Mode == StudyMode.Recall && !Revealed && !Done);
        PauseCommand = new AsyncRelayCommand(TogglePauseAsync, () => (InSession || IsPaused) && !Done);
        ResumeCommand = new AsyncRelayCommand(ResumeAsync, () => IsPaused && _services.Study.IsActive);
        GradeCommand = new AsyncRelayCommand<string>(
            g => AnswerAsync((Grade)int.Parse(g ?? "0")),
            _ => CanRate);
        EndCommand = new AsyncRelayCommand(EndAsync, () => InSession || IsPaused);
        SpeakCommand = new AsyncRelayCommand(() => _services.Speech.SpeakAsync(Lemma), () => ShowWordSpeak);
        SubmitInputCommand = new AsyncRelayCommand(SubmitInputAsync, () => CanSubmitInput);
        ContinueCommand = new AsyncRelayCommand(ContinueAfterAnswerAsync, () => ShowContinue);
        DictationReplayCommand = new AsyncRelayCommand(ReplayDictationAsync, () => ShowDictationReplay);
        FourGradeMode = services.FourGradeMode;
    }

    /// <summary>Called when navigated in with an active session (started from Today). UI thread entry.</summary>
    public async Task ResumeAsync()
    {
        if (!_services.Study.IsActive || InSession)
            return;
        await _services.Study.ResumeAsync();
        IsPaused = false;
        InSession = true;
        Done = false;
        Mode = _services.Study.CurrentMode;
        _totalPlanned = Math.Max(1, _services.Study.CurrentPlan?.TotalCards ?? 1);
        UpdateProgress();
        await LoadNextAsync();
    }

    private void Reveal()
    {
        if (!InSession || Mode != StudyMode.Recall || Revealed || Done || Current is null)
            return;
        _services.Study.RevealCard(Current.CardId);
        Revealed = true;
        OnCanExecuteChanged();
    }

    private async Task PauseAsync()
    {
        if (!InSession || Done)
            return;
        await _services.Study.PauseAsync();
        IsPaused = _services.Study.IsPaused;
        InSession = false;
        Waiting = false;
        UpdateProgress();
    }

    private Task TogglePauseAsync() => IsPaused ? ResumeAsync() : PauseAsync();

    private Task AnswerAsync(Grade grade)
    {
        if (!CanRate || Current is null)
            return Task.CompletedTask;
        var current = Current;

        // Whole answer flow runs on the UI thread so ObservableProperty raises on the right
        // thread; the SQLite write inside is awaited asynchronously and never blocks.
        return _services.DispatchAsync(async () =>
        {
            int previousReviews = _services.Study.CompletedReviews;
            await _services.Study.AnswerAsync(current, grade);
            if (_services.Study.CompletedReviews == previousReviews)
                return;

            Revealed = false;
            UpdateProgress();
            await LoadNextAsync();
        });
    }

    /// <summary>Consumes the next due card from the session queue. UI thread entry.</summary>
    private async Task LoadNextAsync()
    {
        UpdateProgress();
        var next = _services.Study.PeekNext();
        if (next is null)
        {
            if (_services.Study.Remaining > 0)
            {
                Waiting = true;
                UpdateWaitText();
            }
            else
            {
                await EndAsync();
            }
            return;
        }

        InputText = "";
        InputFeedback = "";
        InputCorrect = false;
        AnswerSubmitted = false;
        WrongAnswerShown = false;
        SelfTestAnswered = false;
        SelfTestFeedback = "";
        SelfTestAvailable = false;
        SpeechError = "";
        SelfTestChoices.Clear();
        Current = next;
        MarkFeedback = "";
        try
        {
            var marks = await _services.Db.GetCardMarksAsync(next.CardId);
            IsFavorite = marks.IsFavorite;
            IsMastered = marks.IsMastered;
        }
        catch (Exception error)
        {
            StartupFailureReporter.WriteTechnicalError(error, AppServices.StartupLogPath, "loading study card marks failed");
            IsFavorite = false;
            IsMastered = false;
        }
        Lemma = next.Lemma;
        Ipa = next.IpaUs ?? "";
        IpaUk = next.IpaUk ?? "";
        NewBadge = next.IsNew ? "NEW" : "";
        Senses.Clear();
        LoadDetailSections(next.Details);
        HasMeaning = false;
        foreach (var sense in next.Senses)
        {
            var hasSenseMeaning = !string.IsNullOrWhiteSpace(sense.ZhGloss) || !string.IsNullOrWhiteSpace(sense.EnGloss);
            if (!hasSenseMeaning)
                continue;

            HasMeaning = true;
            var gloss = string.Join("  ", new[] { sense.Pos, sense.ZhGloss, sense.EnGloss }.Where(s => !string.IsNullOrWhiteSpace(s)));
            var example = sense.ExampleEn is null && sense.ExampleZh is null
                ? null
                : $"{sense.ExampleEn}　{sense.ExampleZh}".Trim();
            if (gloss.Length > 0 || example is not null)
                Senses.Add(new SenseLine(gloss, example));
        }
        MeaningPrompt = string.Join(Environment.NewLine, Senses.Select(sense => sense.Gloss));
        Revealed = Mode == StudyMode.Recall && _services.Study.IsCardRevealed(next.CardId);
        Waiting = false;
        if (Mode == StudyMode.SelfTest)
            await LoadSelfTestChoicesAsync(next);
        CardChanged?.Invoke();
        OnCanExecuteChanged();
        if (Mode == StudyMode.Dictation)
            await ReplayDictationAsync();
        else if (_services.AutoPronounce && Mode != StudyMode.SpellFromMeaning)
            _ = _services.Speech.SpeakAsync(next.Lemma);
    }

    private void LoadDetailSections(WordDetails details)
    {
        DetailSections.Clear();
        foreach (var section in WordDetailSection.From(details))
            DetailSections.Add(section);
    }

    private async Task LoadSelfTestChoicesAsync(StudyItem item)
    {
        try
        {
            string? correct = FirstGloss(item.Senses);
            var card = await _services.Db.GetCardAsync(item.CardId);
            if (card is null || correct is null)
            {
                SelfTestFeedback = "此卡缺少释义或词书信息，无法自测。请结束本次学习并选择其他模式。";
                return;
            }

            var deck = (await _services.Db.ListDecksAsync()).FirstOrDefault(candidate => candidate.Id == card.DeckId);
            if (deck is null)
            {
                SelfTestFeedback = "此卡缺少释义或词书信息，无法自测。请结束本次学习并选择其他模式。";
                return;
            }

            var words = await _services.Db.SearchWordsAsync("", Math.Max(1, deck.WordCount), deck.Id);
            var choices = new List<SelfTestChoice> { new(correct, IsCorrect: true) };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { correct };
            foreach (var candidate in words)
            {
                if (candidate.Id == card.WordId || FirstGloss(candidate.Senses) is not { } gloss || !seen.Add(gloss))
                    continue;
                choices.Add(new SelfTestChoice(gloss, IsCorrect: false));
                if (choices.Count == 4)
                    break;
            }

            if (choices.Count < 4)
            {
                SelfTestFeedback = "此卡缺少三个不同的词义作为干扰项，无法自测。请结束本次学习并选择其他模式。";
                return;
            }

            for (int index = choices.Count - 1; index > 0; index--)
            {
                int swapIndex = Random.Shared.Next(index + 1);
                (choices[index], choices[swapIndex]) = (choices[swapIndex], choices[index]);
            }
            foreach (var choice in choices)
                SelfTestChoices.Add(choice);
            SelfTestAvailable = true;
        }
        catch (Exception error)
        {
            StartupFailureReporter.WriteTechnicalError(error, AppServices.StartupLogPath, "loading self-test choices failed");
            SelfTestFeedback = "自测选项加载失败。请结束本次学习并重试。";
        }
    }

    private static string? FirstGloss(IReadOnlyList<WordSense> senses)
    {
        foreach (var sense in senses)
        {
            var gloss = string.Join(" · ", new[] { sense.ZhGloss, sense.EnGloss }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            if (gloss.Length > 0)
                return gloss;
        }
        return null;
    }

    private async Task SubmitInputAsync()
    {
        if (!CanSubmitInput || Current is null)
            return;

        var current = Current;
        bool correct = string.Equals(InputText.Trim(), current.Lemma.Trim(), StringComparison.OrdinalIgnoreCase);
        if (correct)
        {
            InputCorrect = true;
            AnswerSubmitted = true;
            InputFeedback = "拼写正确，请选择记忆程度。";
            NotifyPracticePresentationChanged();
            return;
        }

        try
        {
            await _services.DispatchAsync(async () =>
            {
                int previousReviews = _services.Study.CompletedReviews;
                await _services.Study.AnswerAsync(current, Grade.Again, new StudyAnswerContext(IncorrectInput: true));
                if (_services.Study.CompletedReviews == previousReviews)
                    return;

                AnswerSubmitted = true;
                WrongAnswerShown = true;
                InputCorrect = false;
                InputFeedback = $"答案不匹配，正确拼写是：{current.Lemma}";
                UpdateProgress();
                NotifyPracticePresentationChanged();
            });
        }
        catch (Exception error)
        {
            StartupFailureReporter.WriteTechnicalError(error, AppServices.StartupLogPath, "saving typed study answer failed");
            await _services.DispatchAsync(() => InputFeedback = "答案保存失败，请再次提交。请检查数据后重试。");
        }
    }

    public async Task SelectSelfTestChoiceAsync(SelfTestChoice choice)
    {
        if (!ShowSelfTestPanel || SelfTestAnswered || Current is null)
            return;

        var current = Current;
        try
        {
            await _services.DispatchAsync(async () =>
            {
                int previousReviews = _services.Study.CompletedReviews;
                await _services.Study.AnswerAsync(current, choice.IsCorrect ? Grade.Good : Grade.Again);
                if (_services.Study.CompletedReviews == previousReviews)
                    return;

                SelfTestAnswered = true;
                SelfTestFeedback = choice.IsCorrect
                    ? "回答正确 · 已按 Good 记录。"
                    : $"回答错误 · 正确释义：{SelfTestChoices.First(option => option.IsCorrect).Text} · 已按 Again 记录。";
                UpdateProgress();
                NotifyPracticePresentationChanged();
            });
        }
        catch (Exception error)
        {
            StartupFailureReporter.WriteTechnicalError(error, AppServices.StartupLogPath, "saving self-test answer failed");
            await _services.DispatchAsync(() => SelfTestFeedback = "答案保存失败，请重试。");
        }
    }

    private async Task ContinueAfterAnswerAsync()
    {
        if (!ShowContinue)
            return;
        await _services.DispatchAsync(async () =>
        {
            WrongAnswerShown = false;
            SelfTestAnswered = false;
            Revealed = false;
            await LoadNextAsync();
        });
    }

    private async Task ReplayDictationAsync()
    {
        if (Current is null || Mode != StudyMode.Dictation || !ShowCard)
            return;
        SpeechError = "";
        if (!await _services.Speech.TrySpeakAsync(Current.Lemma).ConfigureAwait(false))
            await _services.DispatchAsync(() => SpeechError = "语音播放失败；可以重播，或直接输入答案继续。").ConfigureAwait(false);
    }

    public void SetInputFocus(bool focused) => InputFocused = focused;

    public async Task SetCurrentMarkAsync(CardMark mark, bool enabled)
    {
        if (Current is null || mark is not (CardMark.Favorite or CardMark.Mastered))
            return;
        try
        {
            await _services.Db.SetCardMarkAsync(Current.CardId, mark, enabled);
            await _services.DispatchAsync(() =>
            {
                if (mark == CardMark.Favorite)
                    IsFavorite = enabled;
                else
                    IsMastered = enabled;
                MarkFeedback = "";
            });
        }
        catch (Exception error)
        {
            StartupFailureReporter.WriteTechnicalError(error, AppServices.StartupLogPath, "saving study card mark failed");
            await _services.DispatchAsync(() =>
            {
                OnPropertyChanged(mark == CardMark.Favorite ? nameof(IsFavorite) : nameof(IsMastered));
                MarkFeedback = "标记保存失败，请重试。";
            });
        }
    }

    /// <summary>Timer tick while resting between short learning steps.</summary>
    public void TickWaiting()
    {
        if (!Waiting || !InSession || Done)
            return;
        var nextDue = _services.Study.NextDueUtc();
        if (nextDue is null)
            return;
        if (nextDue.Value <= DateTime.UtcNow.Add(Lexico.Core.Study.StudyService.DueSlackWindow))
        {
            Waiting = false;
            _ = LoadNextAsync();
        }
        else
        {
            UpdateWaitText();
        }
    }

    private void UpdateWaitText()
    {
        var due = _services.Study.NextDueUtc();
        WaitText = due is null
            ? "整理中…"
            : $"短间隔复习稍后到时（约 {Math.Max(0, (int)Math.Ceiling((due.Value - DateTime.UtcNow).TotalSeconds))} 秒后自动继续）";
    }

    private async Task EndAsync()
    {
        if (!InSession && !IsPaused)
            return;

        UpdateProgress();
        int remaining = RemainingCount;
        var summary = await _services.Study.FinishAsync();
        InSession = false;
        IsPaused = false;
        Waiting = false;
        Done = true;
        SummaryText = remaining == 0 ? "今日计划已完成" : "本次学习已结束";
        SummaryDetail = $"复习 {summary.Reviews} 次 · 忘记 {summary.Again} · 困难 {summary.Hard} · 良好 {summary.Good} · 简单 {summary.Easy}　·　用时 {Math.Max(1, (int)summary.Duration.TotalMinutes)} 分钟";
        if (remaining > 0)
            SummaryDetail += $" · 队列剩余 {remaining} 张";
        SessionFinished?.Invoke();
    }

    private int PlannedCompletedCount => Math.Clamp(_totalPlanned - RemainingCount, 0, _totalPlanned);

    public double ProgressFraction => (double)PlannedCompletedCount / Math.Max(1, _totalPlanned);

    public bool ShowIdle => !InSession && !Done;
    public bool ShowCard => InSession && !Waiting && !Done;
    public bool ShowMarkControls => ShowCard;
    public bool ShowQuestion => ShowCard && Mode == StudyMode.Recall && !Revealed;
    public bool ShowInputPanel => ShowCard && IsInputMode && !(AnswerSubmitted && InputCorrect) && !ShowInputUnavailable;
    public bool ShowInputUnavailable => ShowCard && Mode == StudyMode.SpellFromMeaning && !HasMeaning;
    public bool ShowMeaningPrompt => Mode == StudyMode.SpellFromMeaning;
    public bool ShowLemma => ShowCard && Mode is not (StudyMode.SpellFromMeaning or StudyMode.Dictation);
    public bool ShowWordSpeak => ShowCard && Mode is not (StudyMode.SpellFromMeaning or StudyMode.Dictation);
    public bool ShowSelfTestPanel => ShowCard && Mode == StudyMode.SelfTest && SelfTestAvailable && !SelfTestAnswered;
    public bool ShowSelfTestUnavailable => ShowCard && Mode == StudyMode.SelfTest && !SelfTestAvailable;
    public bool ShowSelfTestResult => ShowCard && Mode == StudyMode.SelfTest && SelfTestAnswered;
    public bool ShowTypedFeedback => ShowCard && IsInputMode && AnswerSubmitted && InputCorrect;
    public bool ShowInputFeedback => !string.IsNullOrWhiteSpace(InputFeedback);
    public bool ShowSpeechError => !string.IsNullOrWhiteSpace(SpeechError);
    public bool ShowDictationReplay => ShowCard && Mode == StudyMode.Dictation;
    public bool ShowAnswerPanel => ShowCard && ((Mode == StudyMode.Recall && Revealed) || (IsInputMode && AnswerSubmitted && InputCorrect));
    public bool ShowMissingMeaning => Mode == StudyMode.Recall && Revealed && !HasMeaning;
    public bool ShowGradeOptions => CanRate;
    public bool CanRate => InSession && !Waiting && !Done &&
        ((Mode == StudyMode.Recall && Revealed && HasMeaning) || (IsInputMode && AnswerSubmitted && InputCorrect));
    public bool CanEditInput => ShowInputPanel && !AnswerSubmitted;
    public bool CanSubmitInput => CanEditInput && !string.IsNullOrWhiteSpace(InputText);
    public bool ShowContinue => InSession && !Waiting && !Done && (WrongAnswerShown || SelfTestAnswered);
    public bool CanChooseSelfTest => ShowSelfTestPanel && !SelfTestAnswered;
    public bool FourGradeShortcutsEnabled => FourGradeMode && CanRate && !InputFocused;
    public bool SimpleGradeShortcutsEnabled => !FourGradeMode && CanRate && !InputFocused;
    public bool RevealShortcutEnabled => ShowQuestion && !InputFocused;
    public bool SpeakShortcutEnabled => ShowWordSpeak && !InputFocused;
    public bool ReplayShortcutEnabled => ShowDictationReplay && !InputFocused;
    public bool PauseShortcutEnabled => (ShowCard || ShowPaused) && !InputFocused;
    public bool EndShortcutEnabled => (InSession || IsPaused) && !Done;
    public bool ShowNewBadge => ShowCard && !string.IsNullOrEmpty(NewBadge);
    public bool ShowPaused => IsPaused && !Done;
    public bool NotFourGradeMode => !FourGradeMode;
    private bool IsInputMode => Mode is StudyMode.FollowWrite or StudyMode.SpellFromMeaning or StudyMode.Dictation;

    partial void OnInSessionChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowIdle));
        NotifyCardPresentationChanged();
        OnCanExecuteChanged();
        NotifyGradeAvailabilityChanged();
    }

    partial void OnWaitingChanged(bool value)
    {
        NotifyCardPresentationChanged();
        OnCanExecuteChanged();
        NotifyGradeAvailabilityChanged();
    }

    partial void OnRevealedChanged(bool value)
    {
        NotifyMeaningPresentationChanged();
    }

    partial void OnModeChanged(StudyMode value)
    {
        NotifyPracticePresentationChanged();
        OnCanExecuteChanged();
    }

    partial void OnIpaChanged(string value) => OnPropertyChanged(nameof(IpaDisplay));

    partial void OnInputTextChanged(string value)
    {
        OnPropertyChanged(nameof(CanSubmitInput));
        SubmitInputCommand.NotifyCanExecuteChanged();
    }

    partial void OnInputFocusedChanged(bool value)
    {
        NotifyGradeAvailabilityChanged();
        NotifyPracticePresentationChanged();
    }

    partial void OnInputFeedbackChanged(string value) => OnPropertyChanged(nameof(ShowInputFeedback));

    partial void OnSpeechErrorChanged(string value) => OnPropertyChanged(nameof(ShowSpeechError));

    partial void OnSelfTestFeedbackChanged(string value) => OnPropertyChanged(nameof(ShowSelfTestUnavailable));

    partial void OnAnswerSubmittedChanged(bool value) => NotifyPracticePresentationChanged();

    partial void OnInputCorrectChanged(bool value) => NotifyPracticePresentationChanged();

    partial void OnWrongAnswerShownChanged(bool value) => NotifyPracticePresentationChanged();

    partial void OnSelfTestAvailableChanged(bool value) => NotifyPracticePresentationChanged();

    partial void OnSelfTestAnsweredChanged(bool value) => NotifyPracticePresentationChanged();

    partial void OnNewBadgeChanged(string value) => OnPropertyChanged(nameof(ShowNewBadge));

    partial void OnFourGradeModeChanged(bool value)
    {
        OnPropertyChanged(nameof(KeyboardHint));
        OnPropertyChanged(nameof(NotFourGradeMode));
        NotifyGradeAvailabilityChanged();
    }

    partial void OnDoneChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowIdle));
        NotifyCardPresentationChanged();
        OnCanExecuteChanged();
        NotifyGradeAvailabilityChanged();
    }

    private void NotifyMeaningPresentationChanged()
    {
        OnPropertyChanged(nameof(KeyboardHint));
        OnPropertyChanged(nameof(ShowMissingMeaning));
        NotifyPracticePresentationChanged();
        NotifyGradeAvailabilityChanged();
    }

    private void NotifyGradeAvailabilityChanged()
    {
        OnPropertyChanged(nameof(CanRate));
        OnPropertyChanged(nameof(ShowGradeOptions));
        OnPropertyChanged(nameof(FourGradeShortcutsEnabled));
        OnPropertyChanged(nameof(SimpleGradeShortcutsEnabled));
        OnPropertyChanged(nameof(RevealShortcutEnabled));
        OnPropertyChanged(nameof(SpeakShortcutEnabled));
        OnPropertyChanged(nameof(ReplayShortcutEnabled));
        GradeCommand.NotifyCanExecuteChanged();
    }

    private void NotifyCardPresentationChanged()
    {
        OnPropertyChanged(nameof(ShowCard));
        OnPropertyChanged(nameof(ShowQuestion));
        OnPropertyChanged(nameof(ShowMarkControls));
        OnPropertyChanged(nameof(ShowNewBadge));
        NotifyPracticePresentationChanged();
    }

    private void NotifyPracticePresentationChanged()
    {
        OnPropertyChanged(nameof(ShowQuestion));
        OnPropertyChanged(nameof(ShowMarkControls));
        OnPropertyChanged(nameof(ShowMissingMeaning));
        OnPropertyChanged(nameof(KeyboardHint));
        OnPropertyChanged(nameof(InputPrompt));
        OnPropertyChanged(nameof(ShowInputPanel));
        OnPropertyChanged(nameof(ShowInputUnavailable));
        OnPropertyChanged(nameof(ShowMeaningPrompt));
        OnPropertyChanged(nameof(ShowLemma));
        OnPropertyChanged(nameof(ShowWordSpeak));
        OnPropertyChanged(nameof(ShowSelfTestPanel));
        OnPropertyChanged(nameof(ShowSelfTestUnavailable));
        OnPropertyChanged(nameof(ShowSelfTestResult));
        OnPropertyChanged(nameof(ShowTypedFeedback));
        OnPropertyChanged(nameof(ShowInputFeedback));
        OnPropertyChanged(nameof(ShowSpeechError));
        OnPropertyChanged(nameof(ShowDictationReplay));
        OnPropertyChanged(nameof(ShowAnswerPanel));
        OnPropertyChanged(nameof(CanEditInput));
        OnPropertyChanged(nameof(CanSubmitInput));
        OnPropertyChanged(nameof(ShowContinue));
        OnPropertyChanged(nameof(CanChooseSelfTest));
        OnPropertyChanged(nameof(PauseShortcutEnabled));
        OnPropertyChanged(nameof(EndShortcutEnabled));
        SubmitInputCommand?.NotifyCanExecuteChanged();
        ContinueCommand?.NotifyCanExecuteChanged();
        DictationReplayCommand?.NotifyCanExecuteChanged();
        NotifyGradeAvailabilityChanged();
    }

    /// <summary>Forward window-unfocused spans to the timing collector.</summary>
    public void NotifyUnfocused(double milliseconds)
    {
        if (!IsPaused)
            _services.Study.NotifyUnfocused(milliseconds);
    }

    private void UpdateProgress()
    {
        CompletedCount = _services.Study.CompletedReviews;
        RemainingCount = _services.Study.Remaining;
        ProgressText = $"{PlannedCompletedCount} / {_totalPlanned}";
        OnPropertyChanged(nameof(ProgressFraction));
    }

    private void OnCanExecuteChanged()
    {
        RevealCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
        GradeCommand.NotifyCanExecuteChanged();
        EndCommand.NotifyCanExecuteChanged();
        SpeakCommand.NotifyCanExecuteChanged();
        SubmitInputCommand.NotifyCanExecuteChanged();
        ContinueCommand.NotifyCanExecuteChanged();
        DictationReplayCommand.NotifyCanExecuteChanged();
    }
}
