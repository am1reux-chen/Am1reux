using Xunit;

namespace Lexico.App.Tests;

public sealed class ArticlesOpenPickerTests
{
    [Fact]
    public void ArticleImportPickerAcceptsTypeWordsZip()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Production", "ArticlesPage.xaml.cs"));

        Assert.Contains("FileTypeFilter = { \".txt\", \".json\", \".zip\" }", source);
        Assert.Contains("ImportCommand.ExecuteAsync(file.Path)", source);
    }
}
