using Lexio.Services;
using Lexio.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace Lexio.App.Pages;

/// <summary>Pure visual choice: the Normal tier renders with the emphasized template.</summary>
public sealed class TierTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Normal { get; set; }
    public DataTemplate? Other { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item)
        => item is IntensityOption { Intensity: Lexico.Core.Domain.Intensity.Normal } ? Normal : Other;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
        => SelectTemplateCore(item);
}

public sealed partial class TodayPage : Page
{
    public TodayViewModel Vm { get; }

    public TodayPage()
    {
        InitializeComponent();
        Vm = new TodayViewModel(AppServices.Instance);
        DataContext = Vm;
        Vm.SessionStarted += NavigateToLearn;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (AppServices.Instance.Ready)
        {
            _ = Vm.LoadAsync();
        }
        else
        {
            AppServices.Instance.ReadyChanged += OnReady;
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        AppServices.Instance.ReadyChanged -= OnReady;
    }

    private void OnReady()
    {
        _ = Vm.LoadAsync();
        AppServices.Instance.ReadyChanged -= OnReady;
    }

    private void NavigateToLearn()
    {
        if (App.MainAppWindow is { } window)
        {
            DispatcherQueue.TryEnqueue(() => { if (App.MainAppWindow is MainWindow w) w.NavigateTo(typeof(LearnPage)); });
        }
    }
}
