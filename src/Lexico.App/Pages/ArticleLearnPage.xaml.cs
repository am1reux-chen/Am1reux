using Lexio.Services;
using Lexio.ViewModels;
using Lexico.Core.Domain;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using System.ComponentModel;
using Windows.System;
using Windows.UI.Core;

namespace Lexio.App.Pages;

public sealed partial class ArticleLearnPage : Page
{
    public ArticleLearnViewModel Vm { get; }

    public bool HiddenWhen(bool value) => !value;

    public ArticleLearnPage()
    {
        InitializeComponent();
        Vm = new ArticleLearnViewModel(AppServices.Instance);
        Vm.ReturnRequested += OnReturnRequested;
        Vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is ArticlePracticeRequest request)
            _ = Vm.StartAsync(request.ArticleId, request.Mode);
        else
            Vm.ErrorText = "文章信息无效，请返回 Articles 后重新打开。";
    }

    private async void OnModeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedValue: ArticlePracticeMode mode })
            await Vm.SetModeAsync(mode);
    }

    private async void OnReaderSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListView { SelectedItem: ArticleReaderLine line } || line.Position == Vm.CurrentIndex)
            return;
        if (Vm.IsBusy)
        {
            ArticleReaderList.SelectedItem = Vm.CurrentReaderLine;
            return;
        }

        await Vm.SelectSentenceAsync(line.Position);
        ArticleReaderList.SelectedItem = Vm.CurrentReaderLine;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Vm.CurrentReaderLine) && Vm.CurrentReaderLine is { } line)
            ArticleReaderList.ScrollIntoView(line);
    }

    private void OnTranslationClick(object sender, RoutedEventArgs e) => Vm.TranslationVisible = !Vm.TranslationVisible;

    private async void SentenceInputBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool controlDown = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0;
        if (e.Key != VirtualKey.Enter || !controlDown || !Vm.CanSubmit)
            return;

        e.Handled = true;
        await Vm.SubmitCommand.ExecuteAsync(null);
    }

    private void OnReturnClick(object sender, RoutedEventArgs e) => OnReturnRequested();

    private void OnReturnRequested()
    {
        if (App.MainAppWindow is MainWindow window)
            window.NavigateTo(typeof(ArticlesPage));
    }
}
