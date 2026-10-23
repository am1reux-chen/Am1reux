using Xunit;

namespace Lexico.App.Tests;

public sealed class LibraryBackupPickerTests
{
    [Fact]
    public void FullBackupUsesWindowIdBasedWindowsAppSdkSavePicker()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Production", "LibraryPage.xaml.cs");
        var source = File.ReadAllText(path);
        var start = source.IndexOf("private async void OnCreateBackupClick", StringComparison.Ordinal);
        var end = source.IndexOf("private async void OnRestoreBackupClick", start, StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start, "The full backup handler must remain bounded in LibraryPage.");
        var backupHandler = source[start..end];

        Assert.Contains("new Microsoft.Windows.Storage.Pickers.FileSavePicker(mainWindow.AppWindow.Id)", backupHandler);
        Assert.Contains("FileTypeChoices.Add(\"Lexio 完整数据备份\", [\".lexio-backup\"])", backupHandler);
        Assert.Contains("PickSaveFileAsync()", backupHandler);
    }
}
