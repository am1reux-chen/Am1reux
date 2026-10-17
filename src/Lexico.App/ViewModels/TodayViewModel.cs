using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lexio.Services;
using Lexico.Core.Data;
using Lexico.Core.Domain;
using Lexico.Core.Study;
using Microsoft.UI.Xaml;

namespace Lexio.ViewModels;

public sealed partial class IntensityOption : ObservableObject
{
    public Intensity Intensity { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string ReviewsText { get; init; }
    public required string NewText { get; init; }
    public required string MinutesText { get; init; }
    /// <summary>The page VM's start command; parameter is this option.</summary>
    public required System.Windows.Input.ICommand Start { get; init; }
    public string TagText => Intensity switch
    {
        Intensity.Light => "55% 负载",
        Intensity.Normal => "100% 负载",
        Intensity.Heavy => "170% 负载",
        _ => "",
    };
}

public sealed partial class TodayViewModel : ObservableObject
{
    private readonly AppServices _services;

    [ObservableProperty] private string greeting = "";
    [ObservableProperty] private string dateText = "";
    [ObservableProperty] private string dueText = "";
    [ObservableProperty] private bool loading = true;
    [ObservableProperty] private bool canStart;

    public ObservableCollection<IntensityOption> Options { get; } = [];

    public IAsyncRelayCommand<IntensityOption> StartCommand { get; }

    public event Action? SessionStarted;

    public TodayViewModel(AppServices services)
    {
        _services = services;
        StartCommand = new AsyncRelayCommand<IntensityOption>(StartAsync, _ => CanStart);
    }

    public async Task LoadAsync()
    {
        Loading = true;
        var now = DateTime.Now;
        Greeting = now.Hour switch
        {
            < 5 => "夜深了",
            < 12 => "早上好",
            < 18 => "下午好",
            _ => "晚上好",
        };
        DateText = now.ToString("M月d日 dddd", new System.Globalization.CultureInfo("zh-CN"));

        var nowUtc = DateTime.UtcNow;
        var due = await _services.Db.GetDueSnapshotAsync(nowUtc).ConfigureAwait(false);
        var baseline = await DailyPlanner.LoadBaselinesAsync(_services.Db).ConfigureAwait(false);
        DueText = $"今日到期复习 {due.DueReview + due.DueLearning} · 待学新词 {due.NewAvailable}";

        var options = new List<IntensityOption>();
        foreach (var intensity in new[] { Intensity.Light, Intensity.Normal, Intensity.Heavy })
        {
            var plan = DailyPlanner.Build(intensity, due, baseline);
            options.Add(new IntensityOption
            {
                Intensity = intensity,
                Title = intensity switch
                {
                    Intensity.Light => "轻量",
                    Intensity.Normal => "常规",
                    Intensity.Heavy => "重度",
                    _ => "",
                },
                Description = intensity switch
                {
                    Intensity.Light => "优先巩固高风险记忆",
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
            Options.Clear();
            foreach (var option in options)
                Options.Add(option);
            CanStart = true;
            Loading = false;
            StartCommand.NotifyCanExecuteChanged();
        }).ConfigureAwait(false);
    }

    private async Task StartAsync(IntensityOption? option)
    {
        if (option is null || !CanStart)
            return;
        CanStart = false;
        Loading = true;
        try
        {
            await _services.Study.StartAsync(option.Intensity).ConfigureAwait(false);
            SessionStarted?.Invoke();
        }
        finally
        {
            CanStart = true;
            Loading = false;
        }
    }
}
