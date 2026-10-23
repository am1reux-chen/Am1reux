using Lexio.Services;
using Xunit;

namespace Lexico.App.Tests;

public sealed class AppWindowTitleTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DefaultDataDirectoryKeepsTheProductTitle(string? overridePath)
    {
        Assert.Equal("Lexio", AppWindowTitle.ForDataDirectoryOverride(overridePath));
    }

    [Fact]
    public void CustomDataDirectoryShowsItsNameWithoutItsParentPath()
    {
        const string dataDirectoryName = "Lexio-UI-Verify-20261018";
        var overridePath = Path.Combine(Path.GetTempPath(), dataDirectoryName) + Path.DirectorySeparatorChar;

        Assert.Equal($"Lexio — {dataDirectoryName}", AppWindowTitle.ForDataDirectoryOverride(overridePath));
    }
}
