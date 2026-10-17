using Lexico.Core.Dictionary;
using Xunit;

namespace Lexico.Core.Tests;

public sealed class DictionaryTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lexio-dict-" + Guid.NewGuid().ToString("N"));
    private readonly Data.AppDatabase _db;
    private readonly DictionaryService _dict;

    public DictionaryTests()
    {
        _db = new Data.AppDatabase(Path.Combine(_dir, "dict.db"));
        _db.MigrateAsync().GetAwaiter().GetResult();
        _dict = new DictionaryService(_db);
    }

    [Fact]
    public async Task StarterDeck_SeedsOnce()
    {
        int seeded = await _dict.EnsureStarterDeckAsync();
        Assert.True(seeded >= 90, $"expected ~100 starter words, got {seeded}");

        int again = await _dict.EnsureStarterDeckAsync();
        Assert.Equal(0, again);

        var decks = await _db.ListDecksAsync();
        var starter = decks.Single(d => d.BuiltIn);
        Assert.Equal(DictionaryService.StarterDeckName, starter.Name);
        Assert.Equal(seeded, starter.WordCount);
    }

    [Fact]
    public async Task Search_FindsExactAndPrefix()
    {
        await _dict.EnsureStarterDeckAsync();

        var exact = await _db.SearchWordsAsync("curious", 5);
        Assert.Contains(exact, w => w.Lemma == "curious");

        var prefix = await _db.SearchWordsAsync("be", 20);
        Assert.Contains(prefix, w => w.Lemma == "beautiful");
        Assert.Contains(prefix, w => w.Lemma == "believe");
    }

    [Fact]
    public async Task Search_EmptyQuery_ReturnsNothing()
    {
        await _dict.EnsureStarterDeckAsync();
        Assert.Empty(await _db.SearchWordsAsync("", 10));
        Assert.Empty(await _db.SearchWordsAsync("   ", 10));
    }

    [Fact]
    public async Task CsvImport_HeaderRoundTrip()
    {
        var csv = Path.Combine(_dir, "import.csv");
        await File.WriteAllTextAsync(csv, "word,zh,pos,exEn\r\nserendipity,意外发现的乐趣,n.,A happy accident is serendipity.\r\nephemeral,短暂的,adj.,Fame is often ephemeral.\r\n");

        var result = await _dict.ImportCsvAsync(csv, "My Deck");

        Assert.Null(result.Error);
        Assert.Equal(2, result.Added);

        var found = await _db.SearchWordsAsync("serendipity", 3);
        var word = found.Single(w => w.Lemma == "serendipity");
        Assert.Equal("意外发现的乐趣", word.Senses[0].ZhGloss);
        Assert.Equal("n.", word.Senses[0].Pos);
        Assert.Equal("A happy accident is serendipity.", word.Senses[0].ExampleEn);

        var decks = await _db.ListDecksAsync();
        var deck = decks.Single(d => d.Name == "My Deck");
        Assert.Equal(2, deck.WordCount);
    }

    [Fact]
    public async Task CsvImport_HeaderlessTwoColumns()
    {
        var csv = Path.Combine(_dir, "plain.txt");
        await File.WriteAllTextAsync(csv, "resilience,韧性\nastute,精明的\n");

        var result = await _dict.ImportCsvAsync(csv, "Plain List");

        Assert.Null(result.Error);
        Assert.Equal(2, result.Added);
        var found = await _db.SearchWordsAsync("resilience", 3);
        Assert.Contains(found, w => w.Lemma == "resilience" && w.Senses[0].ZhGloss == "韧性");
    }

    [Fact]
    public void ParseCsvLine_HandlesQuotesAndEscapes()
    {
        var fields = DictionaryService.ParseCsvLine("\"hello, world\",plain,\"say \"\"hi\"\"\"");

        Assert.Equal(3, fields.Count);
        Assert.Equal("hello, world", fields[0]);
        Assert.Equal("plain", fields[1]);
        Assert.Equal("say \"hi\"", fields[2]);
    }

    [Fact]
    public async Task Upsert_SameLemma_UpdatesSenses()
    {
        await _db.UpsertWordAsync(new(0, "testword", null, null, 1, null, [new(0, "n.", "旧释义", null, null, null)]));
        await _db.UpsertWordAsync(new(0, "testword", "/tɛst/", null, 2, null, [new(0, "n.", "新释义", null, null, null)]));

        Assert.Equal(1, await _db.CountWordsAsync());
        var results = await _db.SearchWordsAsync("testword", 3);
        var word = results.Single();
        Assert.Equal("新释义", word.Senses.Single().ZhGloss);
        Assert.Equal("/tɛst/", word.IpaUs);
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
