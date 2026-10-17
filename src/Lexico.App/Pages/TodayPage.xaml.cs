using Lexio.Services;
using Lexio.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace Lexio.App.Pages;

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
