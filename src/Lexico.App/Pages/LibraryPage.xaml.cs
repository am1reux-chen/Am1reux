using Lexio.Services;
using Lexio.ViewModels;
using Lexico.Core.Data;
using Lexico.Core.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;

namespace Lexio.App.Pages;

public sealed partial class LibraryPage : Page
{
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(150) };

    public LibraryViewModel Vm { get; }

    public LibraryPage()
    {
        InitializeComponent();
        Vm = new LibraryViewModel(AppServices.Instance,
            error => StartupFailureReporter.WriteTechnicalError(error, AppServices.StartupLogPath,
                "accessing Library data failed"));
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = Vm.SearchAsync(SearchBox.Text);
        };
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Vm.NoResults) or nameof(Vm.Searching) or nameof(Vm.SelectedDeck) or nameof(Vm.SelectedMarkFilter))
                UpdateListStates();
            if (e.PropertyName == nameof(Vm.DecksLoadError))
                UpdateDecksState();
        };
        Vm.Results.CollectionChanged += (_, _) => UpdateListStates();
        Vm.Decks.CollectionChanged += (_, _) => UpdateDecksState();
        UpdateListStates();
        UpdateDecksState();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (AppServices.Instance.Ready)
            _ = Vm.LoadDecksForDisplayAsync();
        else
            AppServices.Instance.ReadyChanged += OnReady;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        AppServices.Instance.ReadyChanged -= OnReady;
        _debounce.Stop();
    }

    private void OnReady()
    {
        AppServices.Instance.ReadyChanged -= OnReady;
        _ = Vm.LoadDecksForDisplayAsync();
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _debounce.Stop();
        _debounce.Start();
        UpdateListStates();
    }

    private async void OnDeckSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await Vm.SelectDeckAsync(DecksList.SelectedItem as DeckInfo);
    }

    private void OnClearDeckSelectionClick(object sender, RoutedEventArgs e)
    {
        DecksList.SelectedItem = null;
    }

    /// <summary>Pure visual state: initial hint vs empty-result panel vs result list.</summary>
    private void UpdateListStates()
    {
        bool hasResults = Vm.Results.Count > 0;
        bool hasQuery = !string.IsNullOrWhiteSpace(SearchBox.Text);
        bool hasSelectedDeck = Vm.SelectedDeck is not null;
        InitialHintPanel.Visibility = !hasResults && !Vm.NoResults && !hasQuery && !hasSelectedDeck && !Vm.Searching
            ? Visibility.Visible : Visibility.Collapsed;
        EmptyResultsPanel.Visibility = Vm.NoResults && !hasResults
            ? Visibility.Visible : Visibility.Collapsed;
        if (Vm.SelectedMarkFilter != LibraryMarkFilter.All)
        {
            EmptyResultsTitle.Text = $"没有标记为“{LibraryViewModel.MarkFilterName(Vm.SelectedMarkFilter)}”的单词";
            EmptyResultsHint.Text = "学习时可以在卡片上收藏或标记已掌握；输入错误会自动进入错词列表。";
        }
        else
        {
            EmptyResultsTitle.Text = !hasQuery && hasSelectedDeck ? "这本词书还没有单词" : "没有找到匹配的单词";
            EmptyResultsHint.Text = !hasQuery && hasSelectedDeck ? "请选择其他词书，或导入单词" : "换个拼写或更短的关键词试试";
        }
        ResultsList.Visibility = hasResults ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateDecksState()
    {
        var hasError = !string.IsNullOrWhiteSpace(Vm.DecksLoadError);
        DecksLoadErrorPanel.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
        DecksEmptyHint.Visibility = Vm.Decks.Count == 0 && !hasError ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        Vm.StatusText = "正在选择导入文件…";
        var file = await PickerOperation.TryRunAsync(async () =>
        {
            if (App.MainAppWindow is not { } mainWindow)
                throw new InvalidOperationException("The main window is unavailable.");

            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(mainWindow.AppWindow.Id)
            {
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
                FileTypeFilter = { ".csv", ".txt", ".json", ".zip" },
            };
            return await picker.PickSingleFileAsync();
        }, error => ShowOperationFailureAsync(error, "opening dictionary import picker failed",
            "导入未完成", "Lexio 无法打开导入文件选择器。请稍后重试，并查看 Lexio 数据文件夹中的诊断日志。"),
            () => Vm.StatusText = "未选择导入文件；词库未更改。");
        if (file is null)
            return;
        await PickerOperation.TryRunAsync(
            () => AppServices.Instance.RunDataOperationAsync(() => Vm.ImportCommand.ExecuteAsync(file.Path)),
            error => ShowOperationFailureAsync(error, "importing dictionary file failed", "导入未完成",
                "Lexio 无法完成导入。请查看诊断日志，并检查词书列表后再决定是否重试；导入可能已部分完成。"));
        if (Vm.LastImportFeedback is { } feedback)
            await ShowMessageAsync(feedback.Title, feedback.Message);
    }

    private async void OnCreateBackupClick(object sender, RoutedEventArgs e)
    {
        Vm.StatusText = "正在选择备份保存位置…";
        var file = await PickerOperation.TryRunAsync(async () =>
        {
            if (App.MainAppWindow is not { } mainWindow)
                throw new InvalidOperationException("The main window is unavailable.");

            var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(mainWindow.AppWindow.Id)
            {
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"Lexio-full-backup-{DateTime.Now:yyyyMMdd-HHmm}",
            };
            picker.FileTypeChoices.Add("Lexio 完整数据备份", [".lexio-backup"]);

            return await picker.PickSaveFileAsync();
        }, error => ShowOperationFailureAsync(error, "opening full backup save picker failed",
            "备份未完成", "Lexio 无法打开备份保存对话框，尚未创建备份。请稍后重试，并查看 Lexio 数据文件夹中的诊断日志。"),
            () => Vm.StatusText = "已取消备份保存，未创建文件。");
        if (file is null)
            return;

        try
        {
            await AppServices.Instance.RunDataOperationAsync(
                () => AppServices.Instance.DataBackup.CreateAsync(file.Path));
            Vm.StatusText = "完整备份已保存。";
            await ShowMessageAsync("完整备份已保存", $"备份文件：{file.Path}\n\n完整备份包含词库、自定义词书、学习进度、复习历史和设置。复习历史 CSV 只用于分析历史记录，不能替代完整备份。");
        }
        catch (Exception error)
        {
            Vm.StatusText = "备份未完成。";
            StartupFailureReporter.WriteTechnicalError(error, AppServices.StartupLogPath, "creating user backup failed");
            await ShowMessageAsync("备份未完成", "Lexio 无法创建完整备份。当前学习数据没有被替换。请确认目标位置可写，并查看 Lexio 数据文件夹中的诊断日志。");
        }
    }

    private async void OnRestoreBackupClick(object sender, RoutedEventArgs e)
    {
        Vm.StatusText = "正在选择完整备份…";
        var file = await PickerOperation.TryRunAsync(async () =>
        {
            if (App.MainAppWindow is not { } mainWindow)
                throw new InvalidOperationException("The main window is unavailable.");

            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(mainWindow.AppWindow.Id)
            {
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
                FileTypeFilter = { ".lexio-backup", ".backup", ".db" },
            };
            return await picker.PickSingleFileAsync();
        }, error => ShowOperationFailureAsync(error, "opening full backup restore picker failed",
            "恢复未完成", "Lexio 无法打开备份选择对话框，当前数据未被本次操作替换。请稍后重试，并查看 Lexio 数据文件夹中的诊断日志。"),
            () => Vm.StatusText = "未选择备份；当前数据未更改。");
        if (file is null)
            return;

        ContentDialogResult? confirmationResult = null;
        await PickerOperation.TryRunAsync(async () =>
        {
            var confirmation = new ContentDialog
            {
                Title = "恢复完整数据备份？",
                Content = $"将使用“{Path.GetFileName(file.Path)}”替换当前词库、学习进度、复习历史和设置。恢复前，Lexio 会先将当前数据库保存为可保留的安全快照。",
                PrimaryButtonText = "恢复",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            confirmationResult = await confirmation.ShowAsync();
        }, error => ReportDialogFailureAsync(error, "showing user backup restore confirmation failed",
            "无法显示恢复确认对话框；当前数据未更改。"));
        if (confirmationResult is null)
            return;
        if (confirmationResult != ContentDialogResult.Primary)
        {
            Vm.StatusText = "已取消数据恢复；当前数据未更改。";
            return;
        }

        var restoreWindow = App.MainAppWindow as MainWindow;
        restoreWindow?.SetRestoreInProgress(true);
        try
        {
            Vm.StatusText = "正在校验并恢复备份…";
            var result = await AppServices.Instance.RestoreUserDataAsync(file.Path);
            var backupStatus = result.AutomaticBackupUpdated
                ? "自动恢复备份也已更新。"
                : "主数据库已恢复，但自动恢复备份未能更新；诊断详情已写入日志。";
            if (result.AutomaticBackupError is not null)
                StartupFailureReporter.WriteTechnicalError(result.AutomaticBackupError, AppServices.StartupLogPath,
                    "refreshing automatic backup after user restore failed");

            try
            {
                await Vm.LoadDecksAsync();
                await Vm.SearchAsync(SearchBox.Text);
                Vm.StatusText = "数据恢复完成。";
            }
            catch (Exception refreshError)
            {
                Vm.StatusText = "数据已恢复，页面刷新失败；请重启 Lexio。";
                StartupFailureReporter.WriteTechnicalError(refreshError, AppServices.StartupLogPath,
                    "refreshing Library after user restore failed");
                await ShowMessageAsync("数据已恢复，页面刷新失败",
                    $"数据库已恢复，恢复前的数据快照保存在：\n{result.SafetyBackupPath}\n\nLibrary 页面无法重新加载。请关闭并重新打开 Lexio 后继续使用。{backupStatus}");
                RequireRestartAfterRestoreFailure();
                return;
            }

            await ShowMessageAsync("数据恢复完成", $"Lexio 已校验并恢复备份。\n恢复前的数据快照保存在：\n{result.SafetyBackupPath}\n\n{backupStatus}");
        }
        catch (UserDataRestoreRefreshException error)
        {
            Vm.StatusText = "数据已恢复，当前学习状态未同步；请重启 Lexio。";
            StartupFailureReporter.WriteTechnicalError(error, AppServices.StartupLogPath,
                "refreshing application state after user restore failed");
            var result = error.RestoreResult;
            var backupStatus = result.AutomaticBackupUpdated
                ? "自动恢复备份也已更新。"
                : "主数据库已恢复，但自动恢复备份未能更新；诊断详情已写入日志。";
            if (result.AutomaticBackupError is not null)
                StartupFailureReporter.WriteTechnicalError(result.AutomaticBackupError, AppServices.StartupLogPath,
                    "refreshing automatic backup after user restore failed");
            await ShowMessageAsync("数据已恢复，请重启 Lexio",
                $"数据库已恢复，恢复前的数据快照保存在：\n{result.SafetyBackupPath}\n\n当前学习状态未能同步。请关闭并重新打开 Lexio 后继续使用。{backupStatus}");
            RequireRestartAfterRestoreFailure();
        }
        catch (UserDataRestoreException error)
        {
            Vm.StatusText = "备份无法恢复。";
            StartupFailureReporter.WriteTechnicalError(error, AppServices.StartupLogPath, "restoring user backup failed");
            var preservedMessage = error.CurrentDataRestored
                ? "当前数据已从恢复前快照还原。"
                : "当前数据库未能从恢复前快照完成回滚；请勿继续学习，并保留当前文件和快照。";
            await ShowMessageAsync("备份无法恢复", $"所选文件未能通过校验或恢复失败。{preservedMessage}\n快照位置：\n{error.SafetyBackupPath}\n\n请选择有效的 Lexio 完整数据备份。复习历史 CSV 不可用于恢复。");
            if (!error.CurrentDataRestored)
                RequireRestartAfterRestoreFailure();
        }
        catch (Exception error)
        {
            Vm.StatusText = "恢复未完成。";
            StartupFailureReporter.WriteTechnicalError(error, AppServices.StartupLogPath, "preparing user backup restore failed");
            await ShowMessageAsync("恢复未完成", "Lexio 无法准备数据恢复。当前数据库未被本次操作替换；请查看诊断日志。");
        }
        finally
        {
            restoreWindow?.SetRestoreInProgress(false);
        }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        await PickerOperation.TryRunAsync(async () =>
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            await dialog.ShowAsync();
        }, error => ReportDialogFailureAsync(error, "showing Library message dialog failed",
            $"{title}提示未能显示；请检查诊断日志。"));
    }

    private Task ShowOperationFailureAsync(Exception error, string operation, string title, string message)
    {
        Vm.StatusText = title + "。";
        StartupFailureReporter.WriteTechnicalError(error, AppServices.StartupLogPath, operation);
        return ShowMessageAsync(title, message);
    }

    private Task ReportDialogFailureAsync(Exception error, string operation, string status)
    {
        StartupFailureReporter.WriteTechnicalError(error, AppServices.StartupLogPath, operation);
        Vm.StatusText = status;
        return Task.CompletedTask;
    }

    private void RequireRestartAfterRestoreFailure()
    {
        if (App.MainAppWindow is MainWindow window)
            window.RequireRestartAfterRestoreFailure();
    }

    private async void OnWordClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SearchResultItem item)
            await AppServices.Instance.Speech.SpeakAsync(item.Word.Lemma);
    }

    private async void OnMarkToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { DataContext: SearchResultItem item, Tag: string markName } toggle ||
            !Enum.TryParse<CardMark>(markName, out var mark))
            return;
        await Vm.SetMarkAsync(item.CardId, mark, toggle.IsChecked == true);
    }
}
