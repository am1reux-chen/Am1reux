using Lexio.Services;
using Lexio.ViewModels;
using Lexico.Core.Domain;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI.ViewManagement;

namespace Lexio.App.Pages;

public sealed partial class LearnPage : Page
{
    private readonly DispatcherTimer _waitTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly UISettings _uiSettings = new();
    private long? _deactivatedAt;
    private long _lastCardShownAt;

    public LearnViewModel Vm { get; }

    /// <summary>x:Bind functions. Work around derived VM properties that do not
    /// forward change notifications; x:Bind re-evaluates these whenever the
    /// referenced VM property (INPC) changes. They must be instance methods and
    /// must return Visibility directly — the compiler's implicit bool→Visibility
    /// cast on function results generates broken code in this WinUI version.</summary>
    public Visibility HiddenWhen(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public Visibility VisibleWhen(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public Visibility VisibleWhenText(string? value) => string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The card stays mounted under the pause overlay so the current word
    /// and its revealed answer remain on screen while paused (ShowCard itself turns
    /// false because the session is paused, not finished).</summary>
    public Visibility CardAreaVisibility(bool showCard, bool showPaused) =>
        showCard || showPaused ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The pause overlay takes precedence over the idle panel
    /// (ShowIdle is true while paused because InSession is false).</summary>
    public Visibility IdlePanelVisibility(bool showIdle, bool showPaused) =>
        showIdle && !showPaused ? Visibility.Visible : Visibility.Collapsed;

    public LearnPage()
    {
        InitializeComponent();
        Vm = new LearnViewModel(AppServices.Instance);
        _waitTimer.Tick += (_, _) => Vm.TickWaiting();
        Vm.PropertyChanged += OnVmPropertyChanged;
        Loaded += (_, _) => QueueQuestionFocus();
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
        ResetMotion(CardPanel);
        ResetMotion(AnswerPanel);
        _lastCardShownAt = 0;
    }

    /// <summary>Session and command states live in the ViewModel, which notifies
    /// CanExecute for all commands itself; the page only mirrors presentation
    /// (icon/tooltip swap and focus into the overlay's resume button).</summary>
    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Vm.IsPaused))
        {
            bool paused = Vm.IsPaused;
            PauseIcon.Glyph = paused ? "\uE769" : "\uE768";
            ToolTipService.SetToolTip(PauseButton, paused ? "继续 (P)" : "暂停 (P)");
            if (paused)
                DispatcherQueue.TryEnqueue(() => ResumeFromPauseButton.Focus(FocusState.Programmatic));
            else
                DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                {
                    if (!IsLoaded || Vm.IsPaused || !Vm.InSession)
                        return;
                    if (Vm.ShowInputPanel && Vm.CanEditInput)
                        PracticeInputBox.Focus(FocusState.Programmatic);
                    else if (Vm.ShowQuestion)
                        RevealButton.Focus(FocusState.Programmatic);
                    else if (Vm.ShowAnswerPanel)
                        AnswerScroller.Focus(FocusState.Programmatic);
                });
            return;
        }

        if (e.PropertyName == nameof(Vm.Done) && Vm.Done)
        {
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (IsLoaded && Vm.Done)
                    ReturnToTodayButton.Focus(FocusState.Programmatic);
            });
            return;
        }

        if (e.PropertyName is not (nameof(Vm.Revealed) or nameof(Vm.ShowAnswerPanel)))
            return;
        if (Vm.ShowAnswerPanel)
            DispatcherQueue.TryEnqueue(() => { if (Vm.ShowAnswerPanel) AnimateAnswerIn(); });
        else
            ResetMotion(AnswerPanel);
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            _deactivatedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        else if (_deactivatedAt is { } deactivated)
        {
            Vm.NotifyUnfocused(System.Diagnostics.Stopwatch.GetElapsedTime(deactivated).TotalMilliseconds);
            _deactivatedAt = null;
        }
    }

    /// <summary>
    /// Short, interruptible card entrance. Text stays at native scale, including rapid grading.
    /// </summary>
    private void AnimateCardIn()
    {
        ResetMotion(CardPanel);
        ResetMotion(AnswerPanel);
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        bool rapid = _lastCardShownAt != 0 &&
                     (now - _lastCardShownAt) < (long)(0.9 * System.Diagnostics.Stopwatch.Frequency);
        _lastCardShownAt = now;
        QueueQuestionFocus();
        if (!AppServices.Instance.ReduceMotion && _uiSettings.AnimationsEnabled)
            AnimateIn(CardPanel, rapid ? 90 : 160, rapid ? 3f : 8f, rapid ? 0.65f : 0.15f);
    }

    private void QueueQuestionFocus()
    {
        // Navigation can provide the first card before the page has joined the visual tree.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!IsLoaded)
                return;
            if (Vm.ShowInputPanel && Vm.CanEditInput)
                PracticeInputBox.Focus(FocusState.Programmatic);
            else if (Vm.ShowQuestion)
                RevealButton.Focus(FocusState.Programmatic);
        });
    }

    private void PracticeInputBox_GotFocus(object sender, RoutedEventArgs e) => Vm.SetInputFocus(true);

    private void PracticeInputBox_LostFocus(object sender, RoutedEventArgs e) => Vm.SetInputFocus(false);

    private async void PracticeInputBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter || !Vm.CanSubmitInput)
            return;

        e.Handled = true;
        await Vm.SubmitInputCommand.ExecuteAsync(null);
    }

    private async void OnSelfTestChoiceClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SelfTestChoice choice })
            await Vm.SelectSelfTestChoiceAsync(choice);
    }

    private async void OnCurrentMarkToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string markName } toggle ||
            !Enum.TryParse<CardMark>(markName, out var mark))
            return;
        await Vm.SetCurrentMarkAsync(mark, toggle.IsChecked == true);
    }

    /// <summary>The answer moves within its own reserved area; the word does not reflow.</summary>
    private void AnimateAnswerIn()
    {
        ResetMotion(AnswerPanel);
        AnswerScroller.Focus(FocusState.Programmatic);
        if (!AppServices.Instance.ReduceMotion && _uiSettings.AnimationsEnabled)
            AnimateIn(AnswerPanel, 140, 6f, 0.2f);
    }

    private static void ResetMotion(FrameworkElement element)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Translation.Y");
        visual.StopAnimation("Scale");
        visual.Opacity = 1f;
        visual.Scale = System.Numerics.Vector3.One;
        visual.Properties.InsertVector3("Translation", System.Numerics.Vector3.Zero);
    }

    private static void AnimateIn(FrameworkElement element, int durationMs, float offset, float opacity)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        var ease = compositor.CreateCubicBezierEasingFunction(
            new System.Numerics.Vector2(0.2f, 0f),
            new System.Numerics.Vector2(0.25f, 1.0f));
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0.0f, opacity);
        fade.InsertKeyFrame(1.0f, 1.0f, ease);
        fade.Duration = TimeSpan.FromMilliseconds(durationMs);
        visual.StartAnimation("Opacity", fade);
        var rise = compositor.CreateScalarKeyFrameAnimation();
        rise.InsertKeyFrame(0.0f, offset);
        rise.InsertKeyFrame(1.0f, 0f, ease);
        rise.Duration = TimeSpan.FromMilliseconds(durationMs);
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
