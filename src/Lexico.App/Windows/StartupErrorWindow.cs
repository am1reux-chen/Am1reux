using Lexio.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Lexio.App;

internal sealed class StartupErrorWindow : Window
{
    private readonly StartupFailureInfo _info;
    private readonly Func<string, Task<bool>> _restore;
    private readonly TextBlock _status;
    private bool _restoreInProgress;

    public StartupErrorWindow(StartupFailureInfo info, Func<string, Task<bool>> restore)
    {
        _info = info;
        _restore = restore;
        Title = info.Title;

        var title = new TextBlock
        {
            Text = info.Title,
            FontSize = 26,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        var summary = new TextBlock { Text = info.UserSummary, TextWrapping = TextWrapping.Wrap };
        var instructions = new TextBlock { Text = info.RecoveryInstructions, TextWrapping = TextWrapping.Wrap };
        var diagnostics = new TextBlock
        {
            Text = $"诊断日志：{info.LogPath}\n{(info.LogWritten ? "已记录技术详情。" : "日志文件无法写入；请先保留数据库文件。")}",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
        };
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, MinHeight = 20 };

        var actions = new StackPanel { Orientation = Orientation.Vertical, Spacing = 10 };
        var restoreButton = new Button { Content = "选择完整备份并恢复" };
        restoreButton.Click += OnRestoreClick;
        var openLogButton = new Button { Content = "打开数据文件夹" };
        openLogButton.Click += (_, _) => OpenDataFolder();
        var copyButton = new Button { Content = "复制恢复指引" };
        copyButton.Click += (_, _) => CopyInstructions();
        actions.Children.Add(restoreButton);
        actions.Children.Add(openLogButton);
        actions.Children.Add(copyButton);

        var content = new StackPanel
        {
            Spacing = 18,
            Padding = new Thickness(30),
            MaxWidth = 760,
        };
        content.Children.Add(title);
        content.Children.Add(summary);
        content.Children.Add(instructions);
        content.Children.Add(diagnostics);
        content.Children.Add(_status);
        content.Children.Add(actions);
        Content = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if (_restoreInProgress)
            return;

        _restoreInProgress = true;
        var restoreButton = (Button)sender;
        restoreButton.IsEnabled = false;
        try
        {
            await PickerOperation.TryRunAsync(RestoreFromBackupAsync, ReportRestoreFlowFailureAsync);
        }
        finally
        {
            _restoreInProgress = false;
            restoreButton.IsEnabled = true;
        }
    }

    private async Task RestoreFromBackupAsync()
    {
        _status.Text = "正在选择完整备份…";
        var file = await PickerOperation.TryRunAsync(async () =>
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id)
            {
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
                FileTypeFilter = { ".lexio-backup", ".backup", ".db" },
            };
            return await picker.PickSingleFileAsync();
        }, error =>
        {
            StartupFailureReporter.WriteTechnicalError(error, _info.LogPath, "opening startup recovery picker failed");
            _status.Text = "无法打开备份选择器。当前数据库未更改；请检查诊断日志后重试。";
            return Task.CompletedTask;
        }, () => _status.Text = "未选择备份；当前数据库未更改。");
        if (file is null)
            return;

        var confirmation = new ContentDialog
        {
            Title = "确认使用此完整备份",
            Content = $"将从“{Path.GetFileName(file.Path)}”恢复学习数据。当前数据库会先保存到恢复前快照；如果备份校验失败，当前数据会保留。",
            PrimaryButtonText = "恢复",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = ((FrameworkElement)Content).XamlRoot,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            _status.Text = "已取消恢复；当前数据库未更改。";
            return;
        }

        _status.Text = "正在校验并恢复备份…";
        if (await _restore(file.Path))
        {
            Close();
            return;
        }
        _status.Text = "无法从该文件恢复。主数据库仍已保留；请检查诊断日志，或选择另一个 Lexio 完整备份。";
    }

    private Task ReportRestoreFlowFailureAsync(Exception error)
    {
        StartupFailureReporter.WriteTechnicalError(error, _info.LogPath, "startup backup recovery action failed");
        _status.Text = "启动恢复未完成。请保留当前数据库和备份文件，并检查诊断日志后重试。";
        return Task.CompletedTask;
    }

    private void OpenDataFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Path.GetDirectoryName(_info.DatabasePath)!,
                UseShellExecute = true,
            });
        }
        catch (Exception error)
        {
            StartupFailureReporter.WriteTechnicalError(error, _info.LogPath, "opening data folder failed");
            _status.Text = "无法打开数据文件夹。文件位置已显示在上面的恢复指引中。";
        }
    }

    private void CopyInstructions()
    {
        PickerOperation.TryRun(() =>
        {
            var data = new DataPackage();
            data.SetText(_info.RecoveryInstructions + $"\n诊断日志：{_info.LogPath}");
            Clipboard.SetContent(data);
            _status.Text = "恢复指引已复制。";
        }, error =>
        {
            StartupFailureReporter.WriteTechnicalError(error, _info.LogPath, "copying startup recovery instructions failed");
            _status.Text = "无法复制恢复指引。请手动选择上方文字并复制。";
        });
    }
}
