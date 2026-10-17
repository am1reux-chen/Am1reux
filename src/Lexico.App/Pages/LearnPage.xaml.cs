using Lexio.Services;
using Lexio.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Navigation;

namespace Lexio.App.Pages;

public sealed partial class LearnPage : Page
{
    private readonly DispatcherTimer _waitTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private DateTime? _deactivatedAt;
    private long _lastCardShownAt;

    public LearnViewModel Vm { get; }

    /// <summary>x:Bind functions. Work around derived VM properties that do not
    /// forward change notifications; x:Bind re-evaluates these whenever the
    /// referenced VM property (INPC) changes. They must be instance methods and
    /// must return Visibility directly — the compiler's implicit bool→Visibility
    /// cast on function results generates broken code in this WinUI version.</summary>
    public Visibility HiddenWhen(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public Visibility VisibleWhenText(string? value) => string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;

    public LearnPage()
    {
        InitializeComponent();
        Vm = new LearnViewModel(AppServices.Instance);
        _waitTimer.Tick += (_, _) => Vm.TickWaiting();
        Vm.PropertyChanged += OnVmPropertyChanged;
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

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Vm.Revealed) && Vm.Revealed)
            AnimateAnswerIn();
    }

    /// <summary>
    /// Card entrance: opacity + rise + a hint of scale. Interruptible by design — a new card
    /// restarts the animation, and answering never waits on it. When cards come in quick
    /// succession the transition shortens automatically so fast grading never feels laggy.
    /// </summary>
    private void AnimateCardIn()
    {
        if (AppServices.Instance.ReduceMotion)
            return;

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        bool rapid = _lastCardShownAt != 0 &&
                     (now - _lastCardShownAt) < (long)(1.2 * System.Diagnostics.Stopwatch.Frequency);
        _lastCardShownAt = now;

        float durationMs = rapid ? 110f : 240f;
        float offset = rapid ? 10f : 26f;
        float scaleFrom = rapid ? 0.995f : 0.985f;

        var visual = ElementCompositionPreview.GetElementVisual(CardPanel);
        var compositor = visual.Compositor;
        ElementCompositionPreview.SetIsTranslationEnabled(CardPanel, true);
        visual.CenterPoint = new System.Numerics.Vector3(
            (float)(CardPanel.ActualWidth / 2), (float)(CardPanel.ActualHeight / 2), 0f);

        visual.StopAnimation("Opacity");
        visual.StopAnimation("Translation.Y");
        visual.StopAnimation("Scale");
        visual.Opacity = 0f;

        var ease = compositor.CreateCubicBezierEasingFunction(
            new System.Numerics.Vector2(0.25f, 0.1f),
            new System.Numerics.Vector2(0.25f, 1.0f));

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0.0f, 0.0f);
        fade.InsertKeyFrame(1.0f, 1.0f, ease);
        fade.Duration = TimeSpan.FromMilliseconds(rapid ? 90 : 190);
        visual.StartAnimation("Opacity", fade);

        var slide = compositor.CreateScalarKeyFrameAnimation();
        slide.InsertKeyFrame(0.0f, offset);
        slide.InsertKeyFrame(1.0f, 0f, ease);
        slide.Duration = TimeSpan.FromMilliseconds(durationMs);
        visual.StartAnimation("Translation.Y", slide);

        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0.0f, new System.Numerics.Vector3(scaleFrom, scaleFrom, 1f));
        scale.InsertKeyFrame(1.0f, new System.Numerics.Vector3(1f, 1f, 1f), ease);
        scale.Duration = TimeSpan.FromMilliseconds(durationMs);
        visual.StartAnimation("Scale", scale);
    }

    /// <summary>Answer panel expands: fade + slight rise, 190ms, interruptible.</summary>
    private void AnimateAnswerIn()
    {
        if (AppServices.Instance.ReduceMotion)
            return;

        var visual = ElementCompositionPreview.GetElementVisual(AnswerPanel);
        var compositor = visual.Compositor;
        ElementCompositionPreview.SetIsTranslationEnabled(AnswerPanel, true);

        visual.StopAnimation("Opacity");
        visual.StopAnimation("Translation.Y");
        visual.Opacity = 0f;

        var ease = compositor.CreateCubicBezierEasingFunction(
            new System.Numerics.Vector2(0.25f, 0.1f),
            new System.Numerics.Vector2(0.25f, 1.0f));

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0.0f, 0.0f);
        fade.InsertKeyFrame(1.0f, 1.0f, ease);
        fade.Duration = TimeSpan.FromMilliseconds(180);
        visual.StartAnimation("Opacity", fade);

        var rise = compositor.CreateScalarKeyFrameAnimation();
        rise.InsertKeyFrame(0.0f, 14f);
        rise.InsertKeyFrame(1.0f, 0f, ease);
        rise.Duration = TimeSpan.FromMilliseconds(210);
        visual.StartAnimation("Translation.Y", rise);
    }

    private void OnBackToToday(object sender, RoutedEventArgs e)
    {
        if (App.MainAppWindow is MainWindow window)
        {
            window.NavigateTo(typeof(TodayPage));
        }
    }
}
