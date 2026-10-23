using System.Xml.Linq;
using Xunit;

namespace Lexico.App.Tests;

public sealed class DashboardLoadLayoutTests
{
    [Fact]
    public void TodayExplainsWhenNoPlanHasWork()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Production", "TodayPage.xaml");
        var root = XDocument.Load(path).Root!;

        Assert.Contains(root.Descendants(), element =>
            element.Name.LocalName == "TextBlock" &&
            ((string?)element.Attribute("Text"))?.Contains("今天没有待学词卡", StringComparison.Ordinal) == true &&
            ((string?)element.Attribute("Visibility"))?.Contains("NoTasksVisibility(Vm.NoTasks)", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData("TodayPage.xaml")]
    [InlineData("InsightsPage.xaml")]
    public void LoadFailureDisplaysErrorAndOffersRetry(string pageFile)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Production", pageFile);
        var root = XDocument.Load(path).Root!;

        Assert.Contains(root.Descendants(), element =>
            element.Name.LocalName == "TextBlock" &&
            ((string?)element.Attribute("Text"))?.Contains("Vm.LoadError", StringComparison.Ordinal) == true);
        Assert.Contains(root.Descendants(), element =>
            element.Name.LocalName == "Button" &&
            ((string?)element.Attribute("Command"))?.Contains("Vm.RetryCommand", StringComparison.Ordinal) == true);
        Assert.Contains(root.Descendants(), element =>
            ((string?)element.Attribute("Visibility"))?.Contains("LoadErrorVisibility(Vm.LoadError)", StringComparison.Ordinal) == true);
    }
}
