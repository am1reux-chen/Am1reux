using Xunit;

namespace Lexico.App.Tests;

public sealed class AppServicesShutdownTests
{
    [Fact]
    public void ShutdownStopsAndDrainsDataOperationsBeforeDisposingDatabase()
    {
        var source = ReadProductionSource();
        var shutdownStart = source.IndexOf("private async Task ShutdownCoreAsync()", StringComparison.Ordinal);
        Assert.True(shutdownStart >= 0);

        var dataOperationDrain = source.IndexOf("await _dataOperations.StopAndWaitAsync()", shutdownStart,
            StringComparison.Ordinal);
        var databaseDispose = source.IndexOf("await Db.DisposeAsync()", shutdownStart, StringComparison.Ordinal);

        Assert.True(dataOperationDrain > shutdownStart);
        Assert.True(databaseDispose > dataOperationDrain);
    }

    [Fact]
    public void UserRestoreIsRegisteredBeforeItsCoreFlowStarts()
    {
        var source = ReadProductionSource();
        var restoreStart = source.IndexOf("public Task<UserDataRestoreResult> RestoreUserDataAsync(",
            StringComparison.Ordinal);
        Assert.True(restoreStart >= 0);

        var restoreCore = source.IndexOf("private async Task<UserDataRestoreResult> RestoreUserDataCoreAsync(",
            restoreStart, StringComparison.Ordinal);
        Assert.True(restoreCore > restoreStart);
        Assert.Contains("_dataOperations.RunAsync(() => RestoreUserDataCoreAsync(sourcePath))",
            source[restoreStart..restoreCore]);
    }

    [Fact]
    public void LibraryImportsAndBackupsShareTheRestoreAndShutdownGate()
    {
        var source = ReadProductionSource();
        var operationStart = source.IndexOf("public Task RunDataOperationAsync(Func<Task> operation)",
            StringComparison.Ordinal);
        var restoreStart = source.IndexOf("public Task<UserDataRestoreResult> RestoreUserDataAsync(",
            StringComparison.Ordinal);
        Assert.True(operationStart >= 0);
        Assert.True(restoreStart > operationStart);
        Assert.Contains("_dataOperations.RunAsync(operation)", source[operationStart..restoreStart]);
        Assert.Contains("_dataOperations.RunAsync(() => RestoreUserDataCoreAsync(sourcePath))",
            source[restoreStart..]);

        var librarySource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Production", "LibraryPage.xaml.cs"));
        Assert.Contains("RunDataOperationAsync(() => Vm.ImportCommand.ExecuteAsync(file.Path))", librarySource);
        var backupStart = librarySource.IndexOf("private async void OnCreateBackupClick(", StringComparison.Ordinal);
        var backupRestoreStart = librarySource.IndexOf("private async void OnRestoreBackupClick(", backupStart,
            StringComparison.Ordinal);
        Assert.True(backupStart >= 0);
        Assert.True(backupRestoreStart > backupStart);
        var backupFlow = librarySource[backupStart..backupRestoreStart];
        var backupOperation = backupFlow.IndexOf("RunDataOperationAsync(", StringComparison.Ordinal);
        var createBackup = backupFlow.IndexOf("DataBackup.CreateAsync(file.Path)", StringComparison.Ordinal);
        Assert.True(backupOperation >= 0);
        Assert.True(createBackup > backupOperation);
    }

    [Fact]
    public void RestoreCoreAwaitsWithoutCapturingTheUiContext()
    {
        var source = ReadProductionSource();
        var restoreCore = source.IndexOf("private async Task<UserDataRestoreResult> RestoreUserDataCoreAsync(",
            StringComparison.Ordinal);
        Assert.True(restoreCore >= 0);

        var restoreCoreEnd = source.IndexOf("// ---- cached user preferences ----", restoreCore,
            StringComparison.Ordinal);
        Assert.True(restoreCoreEnd > restoreCore);

        var awaitLines = source[restoreCore..restoreCoreEnd]
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains("await ", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(awaitLines);
        Assert.All(awaitLines, line => Assert.Contains(".ConfigureAwait(false)", line));
    }

    [Fact]
    public void UserRestoreDrainsStudyWorkBeforePausingAndReplacingTheDatabase()
    {
        var source = ReadProductionSource();
        var restoreCore = source.IndexOf("private async Task<UserDataRestoreResult> RestoreUserDataCoreAsync(",
            StringComparison.Ordinal);
        Assert.True(restoreCore >= 0);

        var restoreCoreEnd = source.IndexOf("// ---- cached user preferences ----", restoreCore,
            StringComparison.Ordinal);
        Assert.True(restoreCoreEnd > restoreCore);
        var restoreFlow = source[restoreCore..restoreCoreEnd];
        var initialDrain = restoreFlow.IndexOf("await Study.WaitForPendingOperationsAsync()", StringComparison.Ordinal);
        var resumeState = restoreFlow.IndexOf("bool resumeOnFailure =", StringComparison.Ordinal);
        var pause = restoreFlow.IndexOf("await Study.PauseAsync()", StringComparison.Ordinal);
        var pauseDrain = restoreFlow.IndexOf("await Study.WaitForPendingOperationsAsync()", initialDrain + 1,
            StringComparison.Ordinal);
        var replaceDatabase = restoreFlow.IndexOf("DataBackup.RestoreAsync(sourcePath)", StringComparison.Ordinal);

        Assert.True(initialDrain >= 0);
        Assert.True(resumeState > initialDrain);
        Assert.True(pause > resumeState);
        Assert.True(pauseDrain > pause);
        Assert.True(replaceDatabase > pauseDrain);
    }

    private static string ReadProductionSource() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Production", "AppServices.cs"));
}
