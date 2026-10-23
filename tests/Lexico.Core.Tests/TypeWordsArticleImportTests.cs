using System.Text;
using Lexico.Core.Data;
using Lexico.Core.Study;
using Xunit;

namespace Lexico.Core.Tests;

public sealed class TypeWordsArticleImportTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lexio-typewords-articles-" + Guid.NewGuid().ToString("N"));
    private AppDatabase _db = null!;

    public async Task InitializeAsync()
    {
        _db = new AppDatabase(Path.Combine(_directory, "articles.db"));
        await _db.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task ImportsBilingualParagraphsAndSkipsExactDuplicates()
    {
        var path = Path.Combine(_directory, "articles.json");
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(path, """
            [
              {
                "title": "A New Day",
                "titleTranslate": "新的一天",
                "text": "The sun rose.\n\nBirds began to sing.",
                "textTranslate": "太阳升起。\n\n鸟儿开始歌唱。",
                "audioSrc": "https://example.invalid/audio.mp3"
              }
            ]
            """, new UTF8Encoding(false));
        var importer = new TypeWordsArticleImporter(_db);

        var first = await importer.ImportAsync(path);
        var second = await importer.ImportAsync(path);
        var articles = await _db.ListArticlesAsync();

        Assert.Null(first.Error);
        Assert.Equal(1, first.Added);
        Assert.Equal(1, Assert.Single(first.ArticleIds));
        Assert.Null(second.Error);
        Assert.Equal(0, second.Added);
        Assert.Equal(1, second.Skipped);
        var article = Assert.Single(articles);
        Assert.Equal("新的一天", article.TitleTranslation);
        var details = await _db.GetArticleAsync(article.Id);
        Assert.Equal(["The sun rose.", "Birds began to sing."], details!.Sentences.Select(sentence => sentence.Text));
        Assert.Equal(["太阳升起。", "鸟儿开始歌唱。"], details.Sentences.Select(sentence => sentence.Translation));
    }

    [Fact]
    public async Task MismatchedParagraphCountsRejectWholeFileBeforeAnyArticleIsWritten()
    {
        var path = Path.Combine(_directory, "bad-articles.json");
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(path, """
            [
              { "title": "Valid before invalid", "text": "One.\n\nTwo.", "textTranslate": "一。" }
            ]
            """, new UTF8Encoding(false));

        var result = await new TypeWordsArticleImporter(_db).ImportAsync(path);

        Assert.NotNull(result.Error);
        Assert.Empty(result.ArticleIds);
        Assert.Empty(await _db.ListArticlesAsync());
    }

    [Fact]
    public async Task MissingTranslationsRejectWholeFileBeforeAnyArticleIsWritten()
    {
        var path = Path.Combine(_directory, "missing-translations.json");
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(path, """
            [
              { "title": "No translation", "text": "One.\n\nTwo." }
            ]
            """, new UTF8Encoding(false));

        var result = await new TypeWordsArticleImporter(_db).ImportAsync(path);

        Assert.NotNull(result.Error);
        Assert.Empty(result.ArticleIds);
        Assert.Empty(await _db.ListArticlesAsync());
    }
}
