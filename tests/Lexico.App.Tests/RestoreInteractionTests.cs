using System.Xml.Linq;
using Xunit;

namespace Lexico.App.Tests;

public sealed class RestoreInteractionTests
{
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void RestoreDisablesNavigationAndShowsProgressUntilOperationCompletes()
    {
        var windowSource = ReadProductionSource("MainWindow.xaml.cs");
        var setRestoreInProgress = windowSource.IndexOf("public void SetRestoreInProgress(bool isRestoring)",
            StringComparison.Ordinal);
        Assert.True(setRestoreInProgress >= 0);

        var restartFailure = windowSource.IndexOf("public void RequireRestartAfterRestoreFailure()",
            setRestoreInProgress, StringComparison.Ordinal);
        Assert.True(restartFailure > setRestoreInProgress);

        var restoreMethod = windowSource[setRestoreInProgress..restartFailure];
        Assert.Contains("Nav.IsEnabled = !isRestoring && !_restartRequiredAfterRestoreFailure", restoreMethod);
        Assert.Contains("ContentFrame.IsEnabled = !isRestoring && !_restartRequiredAfterRestoreFailure", restoreMethod);
        Assert.Contains("RestoreProgressOverlay.Visibility = isRestoring ? Visibility.Visible : Visibility.Collapsed",
            restoreMethod);
        Assert.Contains("RestoreProgressRing.IsActive = isRestoring", restoreMethod);

        var root = XDocument.Load(ProductionPath("MainWindow.xaml")).Root!;
        var overlay = root.Descendants().SingleOrDefault(element =>
            element.Name.LocalName == "Grid" && (string?)element.Attribute(XamlNamespace + "Name") == "RestoreProgressOverlay");
        Assert.NotNull(overlay);
        var windowContent = root.Elements().Single(element => element.Name.LocalName == "Grid");
        Assert.Equal("RestoreProgressOverlay",
            (string?)windowContent.Elements().Last().Attribute(XamlNamespace + "Name"));
        Assert.Equal("Collapsed", (string?)overlay!.Attribute("Visibility"));
        Assert.Equal("True", (string?)overlay.Attribute("IsHitTestVisible"));
        Assert.Contains(overlay.Descendants(), element =>
            element.Name.LocalName == "ProgressRing" &&
            (string?)element.Attribute(XamlNamespace + "Name") == "RestoreProgressRing");
    }

    [Fact]
    public void RestoreInteractionIsReleasedInFinallyButRemainsLockedWhenRestartIsRequired()
    {
        var pageSource = ReadProductionSource("LibraryPage.xaml.cs");
        var restoreStart = pageSource.IndexOf("private async void OnRestoreBackupClick", StringComparison.Ordinal);
        var nextMethod = pageSource.IndexOf("private async Task ShowMessageAsync", restoreStart,
            StringComparison.Ordinal);
        Assert.True(restoreStart >= 0);
        Assert.True(nextMethod > restoreStart);

        var restoreMethod = pageSource[restoreStart..nextMethod];
        var enterBusyState = restoreMethod.IndexOf("restoreWindow?.SetRestoreInProgress(true)", StringComparison.Ordinal);
        var performRestore = restoreMethod.IndexOf("RestoreUserDataAsync(file.Path)", StringComparison.Ordinal);
        var finallyBlock = restoreMethod.LastIndexOf("finally", StringComparison.Ordinal);
        var leaveBusyState = restoreMethod.LastIndexOf("restoreWindow?.SetRestoreInProgress(false)", StringComparison.Ordinal);

        Assert.True(enterBusyState >= 0);
        Assert.True(performRestore > enterBusyState);
        Assert.True(finallyBlock > performRestore);
        Assert.True(leaveBusyState > finallyBlock);

        var windowSource = ReadProductionSource("MainWindow.xaml.cs");
        var restartStart = windowSource.IndexOf("public void RequireRestartAfterRestoreFailure()",
            StringComparison.Ordinal);
        Assert.True(restartStart >= 0);
        var restartEnd = windowSource.IndexOf("/// <summary>", restartStart, StringComparison.Ordinal);
        Assert.True(restartEnd > restartStart);
        Assert.Contains("_restartRequiredAfterRestoreFailure = true", windowSource[restartStart..restartEnd]);
    }

    private static string ReadProductionSource(string name) => File.ReadAllText(ProductionPath(name));

    private static string ProductionPath(string name) => Path.Combine(AppContext.BaseDirectory, "Production", name);
}
