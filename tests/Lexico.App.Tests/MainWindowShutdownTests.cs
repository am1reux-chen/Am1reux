using Xunit;

namespace Lexico.App.Tests;

public sealed class MainWindowShutdownTests
{
    [Fact]
    public void WindowCloseDrainsAsyncOperationsWithoutBlockingTheUiDispatcher()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Production", "MainWindow.xaml.cs"));
        Assert.Contains("AppWindow.Closing += OnAppWindowClosing;", source);
        Assert.DoesNotContain("ShutdownAsync().GetAwaiter().GetResult()", source);

        var handlerStart = source.IndexOf("private async void OnAppWindowClosing(", StringComparison.Ordinal);
        var handlerEnd = source.IndexOf("public void NavigateTo(", handlerStart, StringComparison.Ordinal);
        Assert.True(handlerStart >= 0);
        Assert.True(handlerEnd > handlerStart);

        var handler = source[handlerStart..handlerEnd];
        var cancelClose = handler.IndexOf("args.Cancel = true", StringComparison.Ordinal);
        var disableContent = handler.IndexOf("ContentFrame.IsEnabled = false", StringComparison.Ordinal);
        var waitForShutdown = handler.IndexOf("await AppServices.Instance.ShutdownAsync()", StringComparison.Ordinal);
        var closeAfterShutdown = handler.IndexOf("Close();", waitForShutdown, StringComparison.Ordinal);

        Assert.True(cancelClose >= 0);
        Assert.True(disableContent > cancelClose);
        Assert.True(waitForShutdown > disableContent);
        Assert.True(closeAfterShutdown > waitForShutdown);
    }
}
