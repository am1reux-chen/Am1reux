using Xunit;

namespace Lexico.App.Tests;

public sealed class StartupErrorWindowTests
{
    [Fact]
    public void RecoveryActionsStackVerticallyForNarrowWindows()
    {
        var sourcePath = Path.Combine(AppContext.BaseDirectory, "Production", "StartupErrorWindow.cs");
        var source = File.ReadAllText(sourcePath);
        var start = source.IndexOf("var actions = new StackPanel", StringComparison.Ordinal);
        var end = source.IndexOf("var content = new StackPanel", start, StringComparison.Ordinal);

        Assert.True(start >= 0);
        Assert.True(end > start);
        var actions = source[start..end];

        Assert.Contains("Orientation = Orientation.Vertical", actions);
        Assert.Contains("actions.Children.Add(restoreButton)", actions);
        Assert.Contains("actions.Children.Add(openLogButton)", actions);
        Assert.Contains("actions.Children.Add(copyButton)", actions);
    }

    [Fact]
    public void RestoreClickRejectsReentryAndReenablesTheButtonAfterCancellationOrFailure()
    {
        var sourcePath = Path.Combine(AppContext.BaseDirectory, "Production", "StartupErrorWindow.cs");
        var source = File.ReadAllText(sourcePath);
        var start = source.IndexOf("private async void OnRestoreClick", StringComparison.Ordinal);
        var end = source.IndexOf("private async Task RestoreFromBackupAsync", start, StringComparison.Ordinal);
        Assert.True(start >= 0);
        Assert.True(end > start);
        var handler = source[start..end];

        var reentryGuard = handler.IndexOf("if (_restoreInProgress)", StringComparison.Ordinal);
        var markInProgress = handler.IndexOf("_restoreInProgress = true", StringComparison.Ordinal);
        var disableButton = handler.IndexOf("restoreButton.IsEnabled = false", StringComparison.Ordinal);
        var restoreFlow = handler.IndexOf("RestoreFromBackupAsync", StringComparison.Ordinal);
        var cleanup = handler.IndexOf("finally", StringComparison.Ordinal);
        var reenableButton = handler.LastIndexOf("restoreButton.IsEnabled = true", StringComparison.Ordinal);
        var clearInProgress = handler.LastIndexOf("_restoreInProgress = false", StringComparison.Ordinal);

        Assert.True(reentryGuard >= 0);
        Assert.True(markInProgress > reentryGuard);
        Assert.True(disableButton > markInProgress);
        Assert.True(restoreFlow > disableButton);
        Assert.True(cleanup > restoreFlow);
        Assert.True(reenableButton > cleanup);
        Assert.True(clearInProgress > cleanup);
    }
}
