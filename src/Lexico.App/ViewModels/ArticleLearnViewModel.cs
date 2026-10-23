using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lexio.Services;
using Lexico.Core.Domain;

namespace Lexio.ViewModels;

public sealed record ArticleReaderLine(int Position, string English, string? Chinese)
{
    public bool HasChinese => !string.IsNullOrWhiteSpace(Chinese);
}

public sealed partial class ArticleLearnViewModel : ObservableObject
{
    private readonly AppServices _services;
    private IReadOnlyList<ArticleSentence> _sentences = [];
    private long _articleId;
    private int _currentIndex;

    [ObservableProperty] public partial bool Started { get; set; }
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isComplete;
    [ObservableProperty] private string articleTitle = "";
    [ObservableProperty] private string titleTranslation = "";
    [ObservableProperty] private ArticleSentence? currentSentence;
    [ObservableProperty] private ArticleReaderLine? currentReaderLine;
    [ObservableProperty] private string inputText = "";
    [ObservableProperty] private bool translationVisible;
    [ObservableProperty] private ArticlePracticeMode mode = ArticlePracticeMode.FollowWrite;
    [ObservableProperty] private string progressText = "";
    [ObservableProperty] private string answerFeedback = "";
    [ObservableProperty] private string errorText = "";

    public IAsyncRelayCommand SubmitCommand { get; }
    public IAsyncRelayCommand PreviousCommand { get; }
    public IAsyncRelayCommand NextCommand { get; }
    public IAsyncRelayCommand ReplayCommand { get; }
    public IAsyncRelayCommand EndCommand { get; }
    public IAsyncRelayCommand RestartCommand { get; }
    public ObservableCollection<ArticleReaderLine> ReaderLines { get; } = [];
    public IReadOnlyList<ArticlePracticeModeChoice> PracticeModes { get; } =
    [
        new(ArticlePracticeMode.FollowWrite, "跟写"),
        new(ArticlePracticeMode.Dictation, "听写"),
    ];

    public bool ShowPractice => Started && !IsComplete && CurrentSentence is not null;
    public bool ShowComplete => Started && IsComplete;
    public bool ShowReader => Started;
    public bool ShowTitleTranslation => !string.IsNullOrWhiteSpace(TitleTranslation);
    public bool ShowLoadError => !Started && !IsBusy;
    public bool ShowTypedSentence => ShowPractice && Mode == ArticlePracticeMode.FollowWrite;
    public bool ShowDictationPrompt => ShowPractice && Mode == ArticlePracticeMode.Dictation;
    public bool ShowReplay => ShowPractice && Mode == ArticlePracticeMode.Dictation;
    public bool ShowTranslationButton => ShowPractice && HasTranslation;
    public bool ShowTranslation => TranslationVisible && !string.IsNullOrWhiteSpace(CurrentSentence?.Translation);
    public bool HasTranslation => !string.IsNullOrWhiteSpace(CurrentSentence?.Translation);
    public string SentenceText => CurrentSentence?.Text ?? "";
    public string TranslationText => CurrentSentence?.Translation ?? "";
    public string InputPrompt => Mode == ArticlePracticeMode.Dictation ? "听完后输入完整句子" : "照着上方句子输入";
    public bool CanSubmit => ShowPractice && !IsBusy && !string.IsNullOrWhiteSpace(InputText);
    public bool CanGoPrevious => ShowPractice && !IsBusy && _currentIndex > 0;
    public bool CanGoNext => ShowPractice && !IsBusy;
    public string NextButtonText => _currentIndex >= _sentences.Count - 1 ? "完成文章" : "跳过 / 下一句";
    public string TranslationButtonText => TranslationVisible ? "隐藏译文" : "显示译文";
    public int CurrentIndex => _currentIndex;

    public event Action? ReturnRequested;

    public ArticleLearnViewModel(AppServices services)
    {
        _services = services;
        SubmitCommand = new AsyncRelayCommand(SubmitAsync, () => CanSubmit);
        PreviousCommand = new AsyncRelayCommand(PreviousAsync, () => CanGoPrevious);
        NextCommand = new AsyncRelayCommand(NextAsync, () => CanGoNext);
        ReplayCommand = new AsyncRelayCommand(ReplayAsync, () => ShowReplay && !IsBusy);
        EndCommand = new AsyncRelayCommand(EndAsync, () => Started && !IsBusy);
        RestartCommand = new AsyncRelayCommand(RestartAsync, () => Started && IsComplete && !IsBusy);
    }

    public async Task StartAsync(long articleId, ArticlePracticeMode selectedMode)
    {
        await SetBusyAsync(true).ConfigureAwait(false);
        try
        {
            var state = await _services.ArticlePractice.StartAsync(articleId, selectedMode).ConfigureAwait(false);
            await _services.DispatchAsync(() => ApplyState(state)).ConfigureAwait(false);
            if (!state.Article.IsComplete && state.Mode == ArticlePracticeMode.Dictation)
                await ReplayAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            StartupFailureReporter.WriteTechnicalError(error, AppServices.StartupLogPath, "starting article practice failed");
            await _services.DispatchAsync(() => ErrorText = "文章练习无法打开，请回到 Articles 后重试。")
                .ConfigureAwait(false);
        }
        finally
        {
            await SetBusyAsync(false).ConfigureAwait(false);
        }
    }

    public async Task SetModeAsync(ArticlePracticeMode mode)
    {
        if (!Enum.IsDefined(mode) || mode == Mode || _articleId == 0)
            return;
        await SetBusyAsync(true).ConfigureAwait(false);
        try
        {
            await _services.ArticlePractice.SavePositionAsync(_articleId, _currentIndex, mode, IsComplete).ConfigureAwait(false);
            await _services.DispatchAsync(() =>
            {
                Mode = mode;
                ErrorText = "";
                NotifyPresentationChanged();
            }).ConfigureAwait(false);
            if (!IsComplete && mode == ArticlePracticeMode.Dictation)
                await ReplayAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            StartupFailureReporter.WriteTechnicalError(error, AppServices.StartupLogPath, "changing article practice mode failed");
            await _services.DispatchAsync(() =>
            {
                OnPropertyChanged(nameof(Mode));
                ErrorText = "练习方式保存失败，当前句子未改变。";
            })
                .ConfigureAwait(false);
        }
        finally
        {
            await SetBusyAsync(false).ConfigureAwait(false);
        }
    }

    private async Task SubmitAsync()
    {
        if (!CanSubmit || CurrentSentence is null)
            return;
        if (!string.Equals(InputText.Trim(), CurrentSentence.Text.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            AnswerFeedback = "句子不匹配；请检查拼写、标点和空格后重试。";
            return;
        }
        await AdvanceAsync().ConfigureAwait(false);
    }

    private Task NextAsync() => AdvanceAsync();

    private async Task AdvanceAsync()
    {
        if (!CanGoNext)
            return;
        int nextIndex = _currentIndex + 1;
        await SetBusyAsync(true).ConfigureAwait(false);
        try
        {
            if (nextIndex >= _sentences.Count)
            {
                await _services.ArticlePractice.CompleteAsync(_articleId, Mode).ConfigureAwait(false);
                await _services.DispatchAsync(() =>
                {
                    _currentIndex = _sentences.Count;
                    IsComplete = true;
                    CurrentSentence = null;
                    UpdateReaderLines();
                    InputText = "";
                    AnswerFeedback = "";
                    ErrorText = "";
                    ProgressText = $"{_sentences.Count} / {_sentences.Count} 句";
                    NotifyPresentationChanged();
                }).ConfigureAwait(false);
            }
            else
            {
                await _services.ArticlePractice.SavePositionAsync(_articleId, nextIndex, Mode).ConfigureAwait(false);
                await _services.DispatchAsync(() => SetCurrentIndex(nextIndex)).ConfigureAwait(false);
                if (Mode == ArticlePracticeMode.Dictation)
                    await ReplayAsync().ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            StartupFailureReporter.WriteTechnicalError(error, AppServices.StartupLogPath, "saving article sentence progress failed");
            await _services.DispatchAsync(() => ErrorText = "进度保存失败，仍停留在当前句。请重试或稍后再跳过。")
                .ConfigureAwait(false);
        }
        finally
        {
            await SetBusyAsync(false).ConfigureAwait(false);
        }
    }

    private async Task PreviousAsync()
    {
        if (!CanGoPrevious)
            return;
        int previousIndex = _currentIndex - 1;
        await SetBusyAsync(true).ConfigureAwait(false);
        try
        {
            await _services.ArticlePractice.SavePositionAsync(_articleId, previousIndex, Mode).ConfigureAwait(false);
            await _services.DispatchAsync(() => SetCurrentIndex(previousIndex)).ConfigureAwait(false);
            if (Mode == ArticlePracticeMode.Dictation)
                await ReplayAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            StartupFailureReporter.WriteTechnicalError(error, AppServices.StartupLogPath, "saving previous article position failed");
            await _services.DispatchAsync(() => ErrorText = "进度保存失败，仍停留在当前句。请重试。")
                .ConfigureAwait(false);
        }
        finally
        {
            await SetBusyAsync(false).ConfigureAwait(false);
        }
    }

    public async Task SelectSentenceAsync(int index)
    {
        if (!Started || IsBusy || index < 0 || index >= _sentences.Count || (index == _currentIndex && !IsComplete))
            return;

        await SetBusyAsync(true).ConfigureAwait(false);
        try
        {
            await _services.ArticlePractice.SavePositionAsync(_articleId, index, Mode).ConfigureAwait(false);
            await _services.DispatchAsync(() => SetCurrentIndex(index)).ConfigureAwait(false);
            if (Mode == ArticlePracticeMode.Dictation)
                await ReplayAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            StartupFailureReporter.WriteTechnicalError(error, AppServices.StartupLogPath, "saving selected article position failed");
            await _services.DispatchAsync(() => ErrorText = "进度保存失败，仍停留在当前句。请重试。")
                .ConfigureAwait(false);
        }
        finally
        {
            await SetBusyAsync(false).ConfigureAwait(false);
        }
    }

    private async Task ReplayAsync()
    {
        if (!ShowReplay || CurrentSentence is null)
            return;
        await SetBusyAsync(true).ConfigureAwait(false);
        try
        {
            bool played = await _services.Speech.TrySpeakAsync(CurrentSentence.Text).ConfigureAwait(false);
            await _services.DispatchAsync(() => ErrorText = played ? "" : "语音播放失败；可以重播或继续输入。")
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            StartupFailureReporter.WriteTechnicalError(error, AppServices.StartupLogPath, "playing article sentence failed");
            await _services.DispatchAsync(() => ErrorText = "语音播放失败；可以重播或继续输入。")
                .ConfigureAwait(false);
        }
        finally
        {
            await SetBusyAsync(false).ConfigureAwait(false);
        }
    }

    private async Task EndAsync()
    {
        if (!Started || IsBusy)
            return;
        bool saved = true;
        if (!IsComplete)
        {
            await SetBusyAsync(true).ConfigureAwait(false);
            try
            {
                await _services.ArticlePractice.SavePositionAsync(_articleId, _currentIndex, Mode).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                saved = false;
                StartupFailureReporter.WriteTechnicalError(error, AppServices.StartupLogPath, "saving article position before exit failed");
                await _services.DispatchAsync(() => ErrorText = "进度保存失败，仍停留在当前句。请重试保存后再返回。")
                    .ConfigureAwait(false);
            }
            finally
            {
                await SetBusyAsync(false).ConfigureAwait(false);
            }
        }
        if (saved)
            await _services.DispatchAsync(() => ReturnRequested?.Invoke()).ConfigureAwait(false);
    }

    private async Task RestartAsync()
    {
        if (!Started || !IsComplete || IsBusy)
            return;
        await SetBusyAsync(true).ConfigureAwait(false);
        try
        {
            var state = await _services.ArticlePractice.RestartAsync(_articleId, Mode).ConfigureAwait(false);
            await _services.DispatchAsync(() => ApplyState(state)).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            StartupFailureReporter.WriteTechnicalError(error, AppServices.StartupLogPath, "restarting article practice failed");
            await _services.DispatchAsync(() => ErrorText = "文章重新开始失败，请重试。")
                .ConfigureAwait(false);
        }
        finally
        {
            await SetBusyAsync(false).ConfigureAwait(false);
        }
    }

    private async Task SetBusyAsync(bool value) =>
        await _services.DispatchAsync(() =>
        {
            IsBusy = value;
            NotifyPresentationChanged();
        }).ConfigureAwait(false);

    private void ApplyState(ArticlePracticeState state)
    {
        _articleId = state.Article.Id;
        _sentences = state.Sentences;
        ArticleTitle = state.Article.Title;
        TitleTranslation = state.Article.TitleTranslation ?? "";
        Mode = state.Mode;
        Started = true;
        IsComplete = state.Article.IsComplete || state.CurrentSentence >= state.Sentences.Count;
        _currentIndex = IsComplete ? state.Sentences.Count : Math.Clamp(state.CurrentSentence, 0, state.Sentences.Count - 1);
        CurrentSentence = IsComplete ? null : _sentences[_currentIndex];
        UpdateReaderLines();
        InputText = "";
        TranslationVisible = false;
        AnswerFeedback = "";
        ErrorText = "";
        UpdateProgressText();
        NotifyPresentationChanged();
    }

    private void SetCurrentIndex(int index)
    {
        _currentIndex = index;
        CurrentSentence = _sentences[index];
        IsComplete = false;
        UpdateReaderLines();
        InputText = "";
        TranslationVisible = false;
        AnswerFeedback = "";
        ErrorText = "";
        UpdateProgressText();
        NotifyPresentationChanged();
    }

    private void UpdateReaderLines()
    {
        ReaderLines.Clear();
        for (int index = 0; index < _sentences.Count; index++)
        {
            var sentence = _sentences[index];
            ReaderLines.Add(new ArticleReaderLine(index, sentence.Text, sentence.Translation));
        }
        CurrentReaderLine = IsComplete ? null : ReaderLines[_currentIndex];
        OnPropertyChanged(nameof(CurrentIndex));
    }

    private void UpdateProgressText() => ProgressText = _sentences.Count == 0
        ? ""
        : IsComplete
            ? $"{_sentences.Count} / {_sentences.Count} 句"
            : $"{_currentIndex + 1} / {_sentences.Count} 句";

    private void NotifyPresentationChanged()
    {
        OnPropertyChanged(nameof(ShowPractice));
        OnPropertyChanged(nameof(ShowComplete));
        OnPropertyChanged(nameof(ShowReader));
        OnPropertyChanged(nameof(ShowTitleTranslation));
        OnPropertyChanged(nameof(ShowLoadError));
        OnPropertyChanged(nameof(ShowTypedSentence));
        OnPropertyChanged(nameof(ShowDictationPrompt));
        OnPropertyChanged(nameof(ShowReplay));
        OnPropertyChanged(nameof(ShowTranslationButton));
        OnPropertyChanged(nameof(ShowTranslation));
        OnPropertyChanged(nameof(HasTranslation));
        OnPropertyChanged(nameof(SentenceText));
        OnPropertyChanged(nameof(TranslationText));
        OnPropertyChanged(nameof(InputPrompt));
        OnPropertyChanged(nameof(CanSubmit));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(NextButtonText));
        OnPropertyChanged(nameof(TranslationButtonText));
        SubmitCommand.NotifyCanExecuteChanged();
        PreviousCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
        ReplayCommand.NotifyCanExecuteChanged();
        EndCommand.NotifyCanExecuteChanged();
        RestartCommand.NotifyCanExecuteChanged();
    }

    partial void OnInputTextChanged(string value)
    {
        OnPropertyChanged(nameof(CanSubmit));
        SubmitCommand.NotifyCanExecuteChanged();
    }

    partial void OnTranslationVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowTranslation));
        OnPropertyChanged(nameof(TranslationButtonText));
    }

    partial void OnCurrentSentenceChanged(ArticleSentence? value)
    {
        OnPropertyChanged(nameof(HasTranslation));
        OnPropertyChanged(nameof(ShowTranslation));
        OnPropertyChanged(nameof(ShowTranslationButton));
        OnPropertyChanged(nameof(SentenceText));
        OnPropertyChanged(nameof(TranslationText));
    }

    partial void OnTitleTranslationChanged(string value) => OnPropertyChanged(nameof(ShowTitleTranslation));
    partial void OnModeChanged(ArticlePracticeMode value) => NotifyPresentationChanged();
    partial void OnStartedChanged(bool value) => NotifyPresentationChanged();
    partial void OnIsCompleteChanged(bool value) => NotifyPresentationChanged();
    partial void OnIsBusyChanged(bool value) => NotifyPresentationChanged();
}
