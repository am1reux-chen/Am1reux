using Lexio.Services;
using Lexio.ViewModels;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Navigation;

namespace Lexio.App.Pages;

public sealed partial class LearnPage : Page
{
    private readonly DispatcherTimer _waitTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private DateTime? _deactivatedAt;

    public LearnViewModel Vm { get; }

    public LearnPage()
    {
        InitializeComponent();
        Vm = new LearnViewModel(AppServices.Instance);
        _waitTimer.Tick += (_, _) => Vm.TickWaiting();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Vm.CardChanged += AnimateCardIn;
        if (App.MainAppWindow is not null)
            App.MainAppWindow.Activated += OnWindowActivated;
        _waitTimer.Start();
        _ = Vm.ResumeAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        Vm.CardChanged -= AnimateCardIn;
        if (App.MainAppWindow is not null)
            App.MainAppWindow.Activated -= OnWindowActivated;
        _waitTimer.Stop();
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            _deactivatedAt = DateTime.Now;
        }
        else if (_deactivatedAt is { } deactivated)
        {
            Vm.NotifyUnfocused((DateTime.Now - deactivated).TotalMilliseconds);
            _deactivatedAt = null;
        }
    }

    /// <summary>
    /// Composition entrance animation (opacity + slide). Interruptible by design: each card
    /// restarts it, and answering never waits on it — the new content is already committed.
    /// </summary>
    private void AnimateCardIn()
    {
        if (AppServices.Instance.ReduceMotion)
            return;

        var visual = ElementCompositionPreview.GetElementVisual(CardPanel);
        var compositor = visual.Compositor;
        ElementCompositionPreview.SetIsTranslationEnabled(CardPanel, true);

        visual.StopAnimation("Opacity");
        visual.StopAnimation("Translation.Y");
        visual.Opacity = 0f;

        var ease = compositor.CreateCubicBezierEasingFunction(
            new System.Numerics.Vector2(0.25f, 0.1f),
            new System.Numerics.Vector2(0.25f, 1.0f));

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0.0f, 0.0f);
        fade.InsertKeyFrame(1.0f, 1.0f, ease);
        fade.Duration = TimeSpan.FromMilliseconds(190);
        visual.StartAnimation("Opacity", fade);

        var slide = compositor.CreateScalarKeyFrameAnimation();
        slide.InsertKeyFrame(0.0f, 22f);
        slide.InsertKeyFrame(1.0f, 0f, ease);
        slide.Duration = TimeSpan.FromMilliseconds(240);
        visual.StartAnimation("Translation.Y", slide);
    }

    private void OnBackToToday(object sender, RoutedEventArgs e)
    {
        if (App.MainAppWindow is MainWindow window) { window.NavigateTo(typeof(TodayPage)); }
    }
}
