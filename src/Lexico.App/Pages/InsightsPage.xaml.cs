using Lexio.Services;
using Lexio.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Lexio.App.Pages;

public sealed partial class InsightsPage : Page
{
    public InsightsViewModel Vm { get; }

    public InsightsPage()
    {
        InitializeComponent();
        Vm = new InsightsViewModel(AppServices.Instance);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (AppServices.Instance.Ready)
            _ = Vm.LoadAsync();
        else
            AppServices.Instance.ReadyChanged += OnReady;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        AppServices.Instance.ReadyChanged -= OnReady;
    }

    private void OnReady()
    {
        AppServices.Instance.ReadyChanged -= OnReady;
        _ = Vm.LoadAsync();
    }
}
