using Lexio.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace Lexio.App;

public sealed partial class MainWindow : Window
{
    private readonly UISettings _uiSettings = new();
    private bool _restartRequiredAfterRestoreFailure;
    private bool _shutdownInProgress;
    private bool _shutdownCompleted;
    private readonly Dictionary<string, Type> _pages = new()
    {
        ["today"] = typeof(Pages.TodayPage),
        ["learn"] = typeof(Pages.LearnPage),
        ["library"] = typeof(Pages.LibraryPage),
        ["articles"] = typeof(Pages.ArticlesPage),
        ["insights"] = typeof(Pages.InsightsPage),
    };

    public Frame RootFrame => ContentFrame;

    public MainWindow()
    {
        InitializeComponent();

        Title = AppWindowTitle.ForDataDirectoryOverride(Environment.GetEnvironmentVariable("LEXIO_DATA_DIR"));
        AppWindow.Closing += OnAppWindowClosing;

        var titleBar = AppWindow.TitleBar;
        titleBar.IconShowOptions = Microsoft.UI.Windowing.IconShowOptions.HideIconAndSystemMenu;

        Nav.SelectionChanged += (_, e) =>
        {
            if (e.SelectedItemContainer?.Tag is string tag && _pages.TryGetValue(tag, out var page)
                && ContentFrame.CurrentSourcePageType != page)
            {
                ContentFrame.Navigate(page, null, Transition());
            }
        };
        ContentFrame.Navigate(_pages["today"], null, new SuppressNavigationTransitionInfo());
        Nav.SelectedItem = Nav.MenuItems[0];
    }

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_shutdownCompleted)
            return;

        args.Cancel = true;
        if (_shutdownInProgress)
            return;

        _shutdownInProgress = true;
        Nav.IsEnabled = false;
        ContentFrame.IsEnabled = false;
        try
        {
            await AppServices.Instance.ShutdownAsync();
        }
        catch (Exception error)
        {
            try
            {
                StartupFailureReporter.WriteTechnicalError(error, AppServices.StartupLogPath,
                    "application shutdown failed");
            }
            catch (Exception logError)
            {
                System.Diagnostics.Debug.WriteLine($"[Lexio] Shutdown diagnostics failed: {logError.Message}");
            }
        }

        _shutdownCompleted = true;
        Close();
    }

    public void NavigateTo(Type pageType, object? parameter = null)
    {
        var tag = _pages.FirstOrDefault(kv => kv.Value == pageType).Key;
        if (tag is not null)
        {
            var item = Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string?)i.Tag == tag);
            if (item is not null && !ReferenceEquals(Nav.SelectedItem, item))
            {
                Nav.SelectedItem = item;
                return; // SelectionChanged performs the navigation
            }
        }
        ContentFrame.Navigate(pageType, parameter, Transition());
    }

    public void SetRestoreInProgress(bool isRestoring)
    {
        RestoreProgressRing.IsActive = isRestoring;
        RestoreProgressOverlay.Visibility = isRestoring ? Visibility.Visible : Visibility.Collapsed;
        Nav.IsEnabled = !isRestoring && !_restartRequiredAfterRestoreFailure;
        ContentFrame.IsEnabled = !isRestoring && !_restartRequiredAfterRestoreFailure;
    }

    public void RequireRestartAfterRestoreFailure()
    {
        _restartRequiredAfterRestoreFailure = true;
        SetRestoreInProgress(false);
    }

    /// <summary>Native peer-page entrance, respecting app and Windows motion preferences.</summary>
    private NavigationTransitionInfo Transition()
    {
        return AppServices.Instance.ReduceMotion || !_uiSettings.AnimationsEnabled
            ? new SuppressNavigationTransitionInfo()
            : new EntranceNavigationTransitionInfo();
    }
}
