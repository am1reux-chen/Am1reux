using Xunit;

namespace Lexico.App.Tests;

public sealed class LibraryOpenPickerTests
{
    [Fact]
    public void LibraryImportAndRestoreUseWindowBoundAppSdkOpenPickers()
    {
        var source = ReadProductionSource("LibraryPage.xaml.cs");

        Assert.Equal(2, Count(source,
            "new Microsoft.Windows.Storage.Pickers.FileOpenPicker(mainWindow.AppWindow.Id)"));
        Assert.Contains("FileTypeFilter = { \".csv\", \".txt\", \".json\", \".zip\" }", source);
        Assert.Contains("FileTypeFilter = { \".lexio-backup\", \".backup\", \".db\" }", source);
        Assert.Contains("ImportCommand.ExecuteAsync(file.Path)", source);
        Assert.Contains("RestoreUserDataAsync(file.Path)", source);
        Assert.Contains("Path.GetFileName(file.Path)", source);
        Assert.DoesNotContain("WinRT.Interop.InitializeWithWindow.Initialize(picker", source);
    }

    [Fact]
    public void ImportResultIsShownAfterTheImportCommandCompletes()
    {
        var source = ReadProductionSource("LibraryPage.xaml.cs");
        var import = source.IndexOf("await PickerOperation.TryRunAsync(\n            () => AppServices.Instance.RunDataOperationAsync(() => Vm.ImportCommand.ExecuteAsync(file.Path))",
            StringComparison.Ordinal);
        var feedback = source.IndexOf("if (Vm.LastImportFeedback is { } feedback)", StringComparison.Ordinal);

        Assert.True(import >= 0);
        Assert.True(feedback > import);
        Assert.Contains("await ShowMessageAsync(feedback.Title, feedback.Message);", source[feedback..]);
    }

    [Fact]
    public void StartupRecoveryUsesWindowBoundAppSdkOpenPicker()
    {
        var source = ReadProductionSource("StartupErrorWindow.cs");

        Assert.Contains("new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id)", source);
        Assert.Contains("FileTypeFilter = { \".lexio-backup\", \".backup\", \".db\" }", source);
        Assert.Contains("Path.GetFileName(file.Path)", source);
        Assert.Contains("_restore(file.Path)", source);
        Assert.DoesNotContain("WinRT.Interop.InitializeWithWindow.Initialize(picker", source);
    }

    private static string ReadProductionSource(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Production", name))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    private static int Count(string value, string term) =>
        (value.Length - value.Replace(term, string.Empty, StringComparison.Ordinal).Length) / term.Length;
}
