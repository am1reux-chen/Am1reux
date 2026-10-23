using Xunit;

namespace Lexico.App.Tests;

public sealed class LearnFocusTimingTests
{
    [Fact]
    public void WindowDeactivationUsesMonotonicElapsedTime()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Production", "LearnPage.xaml.cs");
        var source = File.ReadAllText(path);
        var handlerStart = source.IndexOf("private void OnWindowActivated", StringComparison.Ordinal);
        Assert.True(handlerStart >= 0, "LearnPage must handle window activation changes.");

        var handlerEnd = source.IndexOf("    /// <summary>", handlerStart, StringComparison.Ordinal);
        Assert.True(handlerEnd > handlerStart, "The activation handler boundary must be identifiable.");
        var handler = source[handlerStart..handlerEnd];

        Assert.Contains("Stopwatch.GetTimestamp()", handler);
        Assert.Contains("Stopwatch.GetElapsedTime(deactivated).TotalMilliseconds", handler);
        Assert.DoesNotContain("DateTime.Now", handler);
    }
}
