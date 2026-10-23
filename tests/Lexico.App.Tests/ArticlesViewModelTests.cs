using System.IO.Compression;
using System.Text;
using Lexio.Services;
using Lexio.ViewModels;
using Lexico.Core.Data;
using Xunit;

namespace Lexico.App.Tests;

public sealed class ArticlesViewModelTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lexio-articles-viewmodel-" + Guid.NewGuid().ToString("N"));
    private AppDatabase _db = null!;
    private AppServices _services = null!;

    public async Task InitializeAsync()
    {
        _db = new AppDatabase(Path.Combine(_directory, "articles.db"));
        await _db.MigrateAsync();
        _services = new AppServices(_db);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task ImportingTypeWordsZipImportsTheRecommendedBilingualArticleBook()
    {
        var archivePath = Path.Combine(_directory, "TypeWords-master.zip");
        Directory.CreateDirectory(_directory);
        using (var archiveStream = File.Create(archivePath))
        using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create))
        {
            AddZipEntry(archive, "TypeWords-master/public/list/recommend_article.json",
                "[{\"name\":\"新概念英语1\",\"url\":\"NCE_1.json\",\"length\":72,\"recommended\":true}]");
            AddZipEntry(archive, "TypeWords-master/public/dicts/en/article/NCE_1.json",
                "[{\"title\":\"Excuse me!\",\"titleTranslate\":\"对不起！\",\"text\":\"Excuse me!\",\"textTranslate\":\"对不起！\"}]");
        }
        var viewModel = new ArticlesViewModel(_services);

        await viewModel.ImportCommand.ExecuteAsync(archivePath);

        Assert.Equal("新概念英语1：已导入 1 篇，跳过 0 篇重复文章。ZIP 清单标记 72 篇，文件实际 1 篇。", viewModel.StatusText);
        var article = Assert.Single(viewModel.Articles);
        Assert.Equal("Excuse me!", article.Title);
        Assert.Equal("对不起！", article.TitleTranslation);
        Assert.Equal("对不起！", (await _db.GetArticleAsync(article.Id))!.Sentences.Single().Translation);
    }

    private static void AddZipEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
