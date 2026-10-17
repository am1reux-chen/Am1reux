using Lexio.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Lexio.App;

public sealed partial class MainWindow : Window
{
    private readonly Dictionary<string, Type> _pages = new()
    {
        ["today"] = typeof(Pages.TodayPage),
        ["learn"] = typeof(Pages.LearnPage),
        ["library"] = typeof(Pages.LibraryPage),
        ["insights"] = typeof(Pages.InsightsPage),
    };

    public Frame RootFrame => ContentFrame;

    public MainWindow()
    {
        InitializeComponent();

        Title = "Lexio";

        var titleBar = AppWindow.TitleBar;
        titleBar.IconShowOptions = Microsoft.UI.Windowing.IconShowOptions.HideIconAndSystemMenu;

        Nav.SelectionChanged += (_, e) =>
        {
            if (e.SelectedItemContainer?.Tag is string tag && _pages.TryGetValue(tag, out var page))
            {
                ContentFrame.Navigate(page, null, Transition());
            }
        };
        ContentFrame.Navigate(_pages["today"], null, new SuppressNavigationTransitionInfo());
    }

    public void NavigateTo(Type pageType)
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
        ContentFrame.Navigate(pageType, null, Transition());
    }

    /// <summary>Page transitions: ~240ms slide; suppressed when the user prefers reduced motion.</summary>
    private static NavigationTransitionInfo Transition()
    {
        return AppServices.Instance.ReduceMotion
            ? new SuppressNavigationTransitionInfo()
            : new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight };
    }
}
