using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lexio.Services;
using Lexico.Core.Domain;

namespace Lexio.ViewModels;

public sealed record SenseLine(string Gloss, string? Example)
{
    public bool HasExample => !string.IsNullOrWhiteSpace(Example);
}

public sealed partial class LearnViewModel : ObservableObject
{
    private readonly AppServices _services;
    private int _totalPlanned;
    private int _answered;

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

    public ObservableCollection<SenseLine> Senses { get; } = [];

    public IRelayCommand RevealCommand { get; }
    public IAsyncRelayCommand<string> GradeCommand { get; }
    public IAsyncRelayCommand EndCommand { get; }
    public IAsyncRelayCommand SpeakCommand { get; }

    /// <summary>Raised whenever a new card is shown (the page runs its entrance animation).</summary>
    public event Action? CardChanged;

    /// <summary>Raised when the session ends (the page shows the summary).</summary>
    public event Action? SessionFinished;

    public LearnViewModel(AppServices services)
    {
        _services = services;
        FourGradeMode = services.FourGradeMode;
        ShowIpa = services.ShowIpa;
        RevealCommand = new RelayCommand(Reveal, () => InSession && !Revealed && !Done);
        GradeCommand = new AsyncRelayCommand<string>(
            g => AnswerAsync((Grade)int.Parse(g ?? "0")),
            _ => InSession && Revealed && !Done);
        EndCommand = new AsyncRelayCommand(EndAsync);
        SpeakCommand = new AsyncRelayCommand(() => _services.Speech.SpeakAsync(Lemma));
    }

    /// <summary>Called when navigated in with an active session (started from Today). UI thread entry.</summary>
    public async Task ResumeAsync()
    {
        if (!_services.Study.IsActive || InSession)
            return;
        InSession = true;
        Done = false;
        _totalPlanned = Math.Max(1, _services.Study.CurrentPlan?.TotalCards ?? 1);
        UpdateProgress();
        await LoadNextAsync();
    }

    private void Reveal()
    {
        if (!InSession || Revealed || Done)
            return;
        Revealed = true;
        OnCanExecuteChanged();
    }

    private Task AnswerAsync(Grade grade)
    {
        if (!InSession || !Revealed || Done || Current is null)
            return Task.CompletedTask;

        // Whole answer flow runs on the UI thread so ObservableProperty raises on the right
        // thread; the SQLite write inside is awaited asynchronously and never blocks.
        return _services.DispatchAsync(async () =>
        {
            Revealed = false;
            await _services.Study.AnswerAsync(Current, grade);
            _answered++;
            UpdateProgress();
            await LoadNextAsync();
        });
    }

    /// <summary>Consumes the next due card from the session queue. UI thread entry.</summary>
    private async Task LoadNextAsync()
    {
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

        Current = next;
        Lemma = next.Lemma;
        Ipa = next.IpaUs ?? "";
        NewBadge = next.IsNew ? "NEW" : "";
        Senses.Clear();
        foreach (var sense in next.Senses)
        {
            var gloss = string.Join("  ", new[] { sense.Pos, sense.ZhGloss, sense.EnGloss }.Where(s => !string.IsNullOrWhiteSpace(s)));
            var example = sense.ExampleEn is null && sense.ExampleZh is null
                ? null
                : $"{sense.ExampleEn}　{sense.ExampleZh}".Trim();
            if (gloss.Length > 0 || example is not null)
                Senses.Add(new SenseLine(gloss, example));
        }
        Revealed = false;
        Waiting = false;
        CardChanged?.Invoke();
        OnCanExecuteChanged();
        if (_services.AutoPronounce)
            _ = _services.Speech.SpeakAsync(next.Lemma);
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
        if (!InSession)
            return;
        var summary = await _services.Study.FinishAsync();
        InSession = false;
        Waiting = false;
        Done = true;
        SummaryText = $"完成 {summary.Reviews} 次复习";
        SummaryDetail = $"忘记 {summary.Again} · 困难 {summary.Hard} · 良好 {summary.Good} · 简单 {summary.Easy}　·　用时 {Math.Max(1, (int)summary.Duration.TotalMinutes)} 分钟";
        SessionFinished?.Invoke();
    }

    public double ProgressFraction => Math.Clamp((double)_answered / Math.Max(1, _totalPlanned), 0, 1);

    public bool ShowIdle => !InSession && !Done;
    public bool ShowCard => InSession && !Waiting && !Done;
    public bool ShowQuestion => ShowCard && !Revealed;
    public bool ShowNewBadge => ShowCard && !string.IsNullOrEmpty(NewBadge);
    public bool NotFourGradeMode => !FourGradeMode;

    partial void OnInSessionChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowIdle));
        OnPropertyChanged(nameof(ShowCard));
    }

    partial void OnWaitingChanged(bool value) => OnPropertyChanged(nameof(ShowCard));

    partial void OnDoneChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowIdle));
        OnPropertyChanged(nameof(ShowCard));
    }

    /// <summary>Forward window-unfocused spans to the timing collector.</summary>
    public void NotifyUnfocused(double milliseconds) => _services.Study.NotifyUnfocused(milliseconds);

    private void UpdateProgress()
    {
        ProgressText = $"{_answered} / {_totalPlanned}";
        OnPropertyChanged(nameof(ProgressFraction));
    }

    private void OnCanExecuteChanged()
    {
        RevealCommand.NotifyCanExecuteChanged();
        GradeCommand.NotifyCanExecuteChanged();
    }
}
