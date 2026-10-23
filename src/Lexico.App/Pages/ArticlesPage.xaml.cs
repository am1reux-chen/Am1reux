using Lexio.Services;
using Lexio.ViewModels;
using Lexico.Core.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Lexio.App.Pages;

public sealed partial class ArticlesPage : Page
{
    public ArticlesViewModel Vm { get; }

    public bool HiddenWhen(bool value) => !value;

    public ArticlesPage()
    {
        InitializeComponent();
        Vm = new ArticlesViewModel(AppServices.Instance,
            error => StartupFailureReporter.WriteTechnicalError(error, AppServices.StartupLogPath,
                "accessing article data failed"));
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Vm.PracticeRequested += OnPracticeRequested;
        if (AppServices.Instance.Ready)
            _ = Vm.LoadAsync();
        else
            AppServices.Instance.ReadyChanged += OnReady;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        Vm.PracticeRequested -= OnPracticeRequested;
        AppServices.Instance.ReadyChanged -= OnReady;
    }

    private void OnReady()
    {
        AppServices.Instance.ReadyChanged -= OnReady;
        _ = Vm.LoadAsync();
    }

    private async void OnArticleSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await Vm.SelectArticleAsync(ArticleList.SelectedItem as ArticleSummary);
    }

    private void OnAddSentencesClick(object sender, RoutedEventArgs e) => Vm.AddSentenceLines();

    private void OnRemoveSentenceClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ArticleEditorSentence sentence })
            Vm.RemoveSentence(sentence);
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        var file = await PickerOperation.TryRunAsync(async () =>
        {
            if (App.MainAppWindow is not { } mainWindow)
                throw new InvalidOperationException("The main window is unavailable.");
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(mainWindow.AppWindow.Id)
            {
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
                FileTypeFilter = { ".txt", ".json", ".zip" },
            };
            return await picker.PickSingleFileAsync();
        }, error =>
        {
            StartupFailureReporter.WriteTechnicalError(error, AppServices.StartupLogPath,
                "opening article import picker failed");
            Vm.StatusText = "无法打开文章导入选择器，请稍后重试。";
            return Task.CompletedTask;
        }, () => Vm.StatusText = "已取消导入。文章列表未更改。");
        if (file is not null)
            await Vm.ImportCommand.ExecuteAsync(file.Path);
    }

    private async void OnDeleteArticleClick(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedArticle is not { } article)
            return;
        var confirmation = new ContentDialog
        {
            Title = "删除文章？",
            Content = $"将删除“{article.Title}”、其中的 {article.SentenceCount} 句和已保存的练习进度。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await confirmation.ShowAsync() == ContentDialogResult.Primary)
            await Vm.DeleteArticleCommand.ExecuteAsync(article);
    }

    private void OnPracticeRequested(ArticlePracticeRequest request)
    {
        if (App.MainAppWindow is MainWindow window)
            window.NavigateTo(typeof(ArticleLearnPage), request);
    }
}
