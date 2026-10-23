using System.IO.Compression;
using System.Text;
using Lexico.Core.Data;
using Lexico.Core.Dictionary;
using Lexico.Core.Study;
using Xunit;

namespace Lexico.Core.Tests;

public sealed class TypeWordsArchiveImportTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lexio-typewords-archive-" + Guid.NewGuid().ToString("N"));
    private AppDatabase _db = null!;

    public async Task InitializeAsync()
    {
        _db = new AppDatabase(Path.Combine(_directory, "archive.db"));
        await _db.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task WordArchiveImportsItsRecommendedBookAndUsesTheCatalogName()
    {
        var path = CreateArchive(
            ("TypeWords-master/public/list/recommend_word.json", """
                [{"name":"CET-4","url":"CET4_T.json","length":1,"recommended":true}]
                """),
            ("TypeWords-master/public/dicts/en/word/CET4_T.json", """
                [{"word":"cancel","phonetic0":"ˈkæns(ə)l","phonetic1":"ˈkæns(ə)l","trans":[{"pos":"v.","cn":"取消"}],"sentences":[{"c":"The customer called to cancel.","cn":"顾客打电话来取消。"}]}]
                """));

        var result = await new DictionaryService(_db).ImportTypeWordsArchiveAsync(path);

        Assert.Null(result.Error);
        Assert.Equal("CET-4", result.DeckName);
        Assert.Equal(1, result.ExpectedCount);
        Assert.Equal(1, result.SourceCount);
        Assert.Equal(1, result.Added);
        var deck = Assert.Single(await _db.ListDecksAsync());
        Assert.Equal("CET-4", deck.Name);
        Assert.Equal("typewords", deck.Source);
        var word = Assert.Single(await _db.SearchWordsAsync("cancel", 5));
        Assert.Single(word.Details!.Sentences);
    }

    [Fact]
    public async Task WordArchiveUpdatesSourceMetadataWhenItReusesAnImportedDeck()
    {
        var csv = Path.Combine(_directory, "legacy.csv");
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(csv, "word,zh\nhello,你好\n");
        await new DictionaryService(_db).ImportCsvAsync(csv, "CET-4");

        var archivePath = CreateArchive(
            ("TypeWords-master/public/list/recommend_word.json", """[{"name":"CET-4","url":"CET4_T.json","recommended":true}]"""),
            ("TypeWords-master/public/dicts/en/word/CET4_T.json", """[{"word":"hello","trans":[{"pos":"interj.","cn":"你好"}]}]"""));

        var result = await new DictionaryService(_db).ImportTypeWordsArchiveAsync(archivePath);

        Assert.Null(result.Error);
        var deck = Assert.Single(await _db.ListDecksAsync());
        Assert.Equal("typewords", deck.Source);
        Assert.Equal("Imported from " + Path.GetFileName(archivePath), deck.Description);
    }

    [Fact]
    public async Task ArticleArchiveImportsItsRecommendedBilingualCollection()
    {
        var path = CreateArchive(
            ("TypeWords-master/public/list/recommend_article.json", """
                [{"name":"新概念英语1","url":"NCE_1.json","length":72,"recommended":true}]
                """),
            ("TypeWords-master/public/dicts/en/article/NCE_1.json", """
                [{"title":"Excuse me!","titleTranslate":"对不起！","text":"Excuse me!\n\nYes?","textTranslate":"对不起。\n\n什么事？"}]
                """));

        var result = await new TypeWordsArticleImporter(_db).ImportArchiveAsync(path);

        Assert.Null(result.Error);
        Assert.Equal("新概念英语1", result.CollectionName);
        Assert.Equal(72, result.ExpectedCount);
        Assert.Equal(1, result.SourceCount);
        Assert.Equal(1, result.Added);
        var article = Assert.Single(await _db.ListArticlesAsync());
        Assert.Equal("对不起！", article.TitleTranslation);
        var details = await _db.GetArticleAsync(article.Id);
        Assert.Equal(["Excuse me!", "Yes?"], details!.Sentences.Select(sentence => sentence.Text));
        Assert.Equal(["对不起。", "什么事？"], details.Sentences.Select(sentence => sentence.Translation));
    }

    [Fact]
    public async Task ArchiveMissingARecommendedDataFileIsRejectedBeforeWrites()
    {
        var path = CreateArchive(
            ("TypeWords-master/public/list/recommend_article.json", """
                [{"name":"新概念英语1","url":"missing.json","recommended":true}]
                """));

        var result = await new TypeWordsArticleImporter(_db).ImportArchiveAsync(path);

        Assert.NotNull(result.Error);
        Assert.Empty(result.ArticleIds);
        Assert.Empty(await _db.ListArticlesAsync());
    }

    [Fact]
    public async Task ArchiveEntriesWithTraversalSegmentsAreRejectedBeforeWrites()
    {
        var path = CreateArchive(
            ("TypeWords-master/../public/list/recommend_word.json", """[{"name":"CET-4","url":"CET4.json","recommended":true}]"""),
            ("TypeWords-master/../public/dicts/en/word/CET4.json", """[{"word":"hello"}]"""));

        var result = await new DictionaryService(_db).ImportTypeWordsArchiveAsync(path);

        Assert.NotNull(result.Error);
        Assert.Empty(await _db.ListDecksAsync());
        Assert.Equal(0, await _db.CountWordsAsync());
    }

    [Fact]
    public async Task ArchiveWithAmbiguousRecommendedBooksIsRejectedBeforeWrites()
    {
        var path = CreateArchive(
            ("TypeWords-master/public/list/recommend_word.json", """
                [
                  {"name":"CET-4","url":"CET4.json","recommended":true},
                  {"name":"CET-6","url":"CET6.json","recommended":true}
                ]
                """),
            ("TypeWords-master/public/dicts/en/word/CET4.json", "[]"),
            ("TypeWords-master/public/dicts/en/word/CET6.json", "[]"));

        var result = await new DictionaryService(_db).ImportTypeWordsArchiveAsync(path);

        Assert.NotNull(result.Error);
        Assert.Empty(await _db.ListDecksAsync());
    }

    [Fact]
    public async Task ArchiveWithOversizedRecommendedContentIsRejectedBeforeWrites()
    {
        var path = CreateArchive(
            ("TypeWords-master/public/list/recommend_article.json", """
                [{"name":"新概念英语1","url":"NCE_1.json","recommended":true}]
                """),
            ("TypeWords-master/public/dicts/en/article/NCE_1.json", new string('x', 16 * 1024 * 1024 + 1)));

        var result = await new TypeWordsArticleImporter(_db).ImportArchiveAsync(path);

        Assert.NotNull(result.Error);
        Assert.Empty(await _db.ListArticlesAsync());
    }

    private string CreateArchive(params (string Name, string Content)[] entries)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".zip");
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
        return path;
    }
}
