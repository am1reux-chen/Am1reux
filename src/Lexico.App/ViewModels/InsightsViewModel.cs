using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Lexio.Services;
using Lexico.Core.Data;

namespace Lexio.ViewModels;

public sealed record BarItem(string Label, int Count, double Height);

public sealed partial class InsightsViewModel : ObservableObject
{
    private readonly AppServices _services;

    [ObservableProperty] private int newCount;
    [ObservableProperty] private int learningCount;
    [ObservableProperty] private int reviewCount;
    [ObservableProperty] private int reviewsToday;
    [ObservableProperty] private string retentionText = "—";
    [ObservableProperty] private string totalReviewsText = "0";
    [ObservableProperty] private string upcomingText = "";
    [ObservableProperty] private bool loading = true;

    public ObservableCollection<BarItem> Bars { get; } = [];

    public InsightsViewModel(AppServices services) => _services = services;

    public async Task LoadAsync()
    {
        Loading = true;
        var data = await _services.Db.GetInsightsAsync(DateTime.UtcNow).ConfigureAwait(false);
        await _services.DispatchAsync(() =>
        {
            NewCount = data.Counts.New;
            LearningCount = data.Counts.Learning;
            ReviewCount = data.Counts.Review;
            ReviewsToday = data.ReviewsToday;
            RetentionText = data.Retention30d >= 0 ? $"{data.Retention30d:P0}" : "—";
            TotalReviewsText = data.TotalReviews.ToString();
            UpcomingText = $"未来 7 天共到期 {data.Next7Days.Sum(d => d.DueCount)} 次";

            const double maxHeight = 120;
            double max = Math.Max(1, data.Next7Days.Max(d => (double)d.DueCount));
            Bars.Clear();
            foreach (var day in data.Next7Days)
            {
                var label = $"{day.LocalDate.ToString("M/d")} {day.LocalDate.ToString("ddd", new System.Globalization.CultureInfo("zh-CN"))}";
                Bars.Add(new BarItem(label, day.DueCount, Math.Max(2, day.DueCount / max * maxHeight)));
            }
            Loading = false;
        }).ConfigureAwait(false);
    }
}
