using Lexico.Core.Dictionary;
using Lexico.Core.Data;
using Lexico.Core.Domain;
using Microsoft.Data.Sqlite;
using System.Text.Json;
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
    public async Task BuiltInVocabularyImport_IsVersionedAdditiveAndPreservesExistingProgress()
    {
        var bookId = await _db.UpsertWordAsync(new WordEntry(0, "book", "/user-book/", null, 7, "B1",
        [new WordSense(0, "n.", "用户释义", "user gloss", "My example.", "用户例句。")]));
        var userDeckId = await _db.EnsureDeckAsync("User Book Deck", null, "csv", builtIn: false);
        await _db.AddWordToDeckAsync(userDeckId, bookId, 0);
        await _db.EnsureCardsForDeckAsync(userDeckId);
        var userCardId = await ExecuteScalarLongAsync("SELECT id FROM cards WHERE deck_id=@deck AND word_id=@word",
            ("@deck", userDeckId), ("@word", bookId));
        await ExecuteNonQueryAsync("""
            UPDATE cards SET state=1, step=3, stability=4.5, difficulty=5.5,
                due='2030-01-02T03:04:05.0000000Z', last_review='2029-12-01T03:04:05.0000000Z',
                reps=8, lapses=2 WHERE id=@id
            """, ("@id", userCardId));

        var firstPack = CreatePack(1, 'a',
            new BuiltInVocabularyDeckImport("common", "常见英语词汇", "常见词", "builtin-vocabulary-v1",
            [
                new WordEntry(0, "book", "/seed-book/", null, 1, null,
                [
                    new WordSense(0, "n.", "用户释义", "seed gloss", null, null),
                    new WordSense(0, "v.", "预订", null, null, null),
                ]),
                new WordEntry(0, "apple", null, null, 2, null,
                    [new WordSense(0, "n.", "苹果", null, null, null)]),
            ]),
            new BuiltInVocabularyDeckImport("ielts", "IELTS 备考核心", "NGSL + NAWL", "builtin-vocabulary-v1",
            [
                new WordEntry(0, "book", null, null, null, null,
                    [new WordSense(0, "n.", "用户释义", null, null, null)]),
                new WordEntry(0, "apple", null, null, null, null,
                    [new WordSense(0, "n.", "苹果", null, null, null)]),
            ]));

        var imported = await _db.ImportBuiltInVocabularyAsync(firstPack);

        Assert.False(imported.AlreadyApplied);
        Assert.Equal(1, imported.WordsAdded);
        Assert.Equal(1, imported.WordsMatched);
        Assert.Equal(2, imported.DecksAdded);
        Assert.Equal(4, imported.DeckWordsAdded);
        Assert.Equal(4, imported.CardsAdded);
        var book = await _db.GetWordAsync(bookId);
        Assert.NotNull(book);
        Assert.Equal("/user-book/", book!.IpaUs);
        Assert.Equal(7, book.FrequencyRank);
        Assert.Equal("B1", book.Cefr);
        Assert.Contains(book.Senses, sense => sense.Pos == "n." && sense.ZhGloss == "用户释义" &&
            sense.EnGloss == "user gloss" && sense.ExampleEn == "My example.");
        Assert.Contains(book.Senses, sense => sense.Pos == "v." && sense.ZhGloss == "预订");

        var repeated = await _db.ImportBuiltInVocabularyAsync(firstPack);
        Assert.True(repeated.AlreadyApplied);
        Assert.Equal(0, repeated.WordsAdded);
        Assert.Equal(0, repeated.DeckWordsAdded);
        Assert.Equal(0, repeated.CardsAdded);

        var secondPack = CreatePack(2, 'b',
            new BuiltInVocabularyDeckImport("common", "常见英语词汇", "常见词", "builtin-vocabulary-v1",
            [
                new WordEntry(0, "book", null, null, 999, "C2",
                    [new WordSense(0, "n.", "覆盖原释义", "replacement", null, null)]),
                new WordEntry(0, "banana", null, null, 3, null,
                    [new WordSense(0, "n.", "香蕉", null, null, null)]),
            ]),
            new BuiltInVocabularyDeckImport("ielts", "IELTS 备考核心", "NGSL + NAWL", "builtin-vocabulary-v1",
            [new WordEntry(0, "banana", null, null, null, null,
                [new WordSense(0, "n.", "香蕉", null, null, null)])]));
        var appended = await _db.ImportBuiltInVocabularyAsync(secondPack);

        Assert.False(appended.AlreadyApplied);
        Assert.Equal(1, appended.WordsAdded);
        Assert.Equal(1, appended.WordsMatched);
        Assert.Equal(2, appended.DeckWordsAdded);
        Assert.Equal(2, appended.CardsAdded);
        var afterAppend = await _db.GetWordAsync(bookId);
        Assert.Equal(7, afterAppend!.FrequencyRank);
        Assert.Equal("B1", afterAppend.Cefr);
        Assert.Contains(afterAppend.Senses, sense => sense.ZhGloss == "用户释义" && sense.EnGloss == "user gloss");
        Assert.Contains(afterAppend.Senses, sense => sense.ZhGloss == "覆盖原释义");

        var storedUserCard = await _db.GetCardAsync(userCardId);
        Assert.NotNull(storedUserCard);
        Assert.Equal(CardState.Review, storedUserCard!.State);
        Assert.Equal(3, storedUserCard.Step);
        Assert.Equal(4.5, storedUserCard.Stability);
        Assert.Equal(5.5, storedUserCard.Difficulty);
        Assert.Equal(8, storedUserCard.Reps);
        Assert.Equal(2, storedUserCard.Lapses);
        Assert.Equal(3, await _db.CountWordsAsync());

        await Assert.ThrowsAsync<InvalidOperationException>(() => _db.ImportBuiltInVocabularyAsync(
            CreatePack(2, 'c', new BuiltInVocabularyDeckImport("common", "常见英语词汇", "常见词", "builtin-vocabulary-v1",
                [new WordEntry(0, "book", null, null, null, null,
                    [new WordSense(0, "n.", "用户释义", null, null, null)])]))));
        var older = await _db.ImportBuiltInVocabularyAsync(firstPack);
        Assert.True(older.AlreadyApplied);
        Assert.Equal(3, await _db.CountWordsAsync());
    }

    [Fact]
    public async Task BuiltInCommonVocabularyShowsTheAsADefiniteArticle()
    {
        await _dict.EnsureBuiltInVocabularyAsync();
        var common = Assert.Single(await _db.ListDecksAsync(), deck => deck.Name == "常见英语词汇");
        var the = Assert.Single(await _db.SearchWordsAsync("the", 5, common.Id), word => word.Lemma == "the");

        Assert.Equal("det.", the.Senses[0].Pos);
        Assert.Equal("定冠词（用于特指的人或事物）", the.Senses[0].ZhGloss);
    }

    [Fact]
    public async Task BuiltInVocabularyV2CorrectsThePrioritySenseAndKeepsSecondarySense()
    {
        var first = CreatePack(1, 'd', new BuiltInVocabularyDeckImport("common", "常见英语词汇", null, "builtin-vocabulary-v1",
        [new WordEntry(0, "the", null, null, 1, null,
        [
            new WordSense(0, "adv.", "愈……愈……", null, null, null),
            new WordSense(0, "adv.", "越……越……", null, null, null),
        ])]));
        await _db.ImportBuiltInVocabularyAsync(first);
        var updated = CreatePack(2, 'e', new BuiltInVocabularyDeckImport("common", "常见英语词汇", null, "builtin-vocabulary-v1",
        [new WordEntry(0, "the", null, null, 1, null,
        [new WordSense(0, "det.", "定冠词（用于特指的人或事物）", null, null, null)])]));

        await _db.ImportBuiltInVocabularyAsync(updated);

        var common = Assert.Single(await _db.ListDecksAsync(), deck => deck.Name == "常见英语词汇");
        var the = Assert.Single(await _db.SearchWordsAsync("the", 5, common.Id), word => word.Lemma == "the");
        Assert.Equal("det.", the.Senses[0].Pos);
        Assert.Equal("定冠词（用于特指的人或事物）", the.Senses[0].ZhGloss);
        Assert.Contains(the.Senses, sense => sense.Pos == "adv." && sense.ZhGloss == "越……越……");
    }

    [Fact]
    public async Task BuiltInVocabularyResource_ImportsTheThreeAuditedDecksAndIsIdempotent()
    {
        var imported = await _dict.EnsureBuiltInVocabularyAsync();

        Assert.False(imported.AlreadyApplied);
        Assert.Equal(21985, imported.WordsAdded);
        Assert.Equal(0, imported.WordsMatched);
        Assert.Equal(3, imported.DecksAdded);
        Assert.Equal(26133, imported.DeckWordsAdded);
        Assert.Equal(26133, imported.CardsAdded);
        var decks = await _db.ListDecksAsync();
        Assert.Contains(decks, deck => deck.Name == "常见英语词汇" && deck.WordCount == 21985);
        Assert.Contains(decks, deck => deck.Name == "大学学术词汇" && deck.WordCount == 753);
        Assert.Contains(decks, deck => deck.Name == "IELTS 备考核心" && deck.WordCount == 3395);

        var again = await _dict.EnsureBuiltInVocabularyAsync();

        Assert.True(again.AlreadyApplied);
        Assert.Equal(0, again.WordsAdded);
        Assert.Equal(0, again.DeckWordsAdded);
        Assert.Equal(0, again.CardsAdded);
        Assert.Equal(21985, await _db.CountWordsAsync());
    }

    [Fact]
    public async Task BuiltInVocabularyImport_UsesDistinctDeckNameWhenUserAlreadyOwnsRequestedName()
    {
        await _db.EnsureDeckAsync("IELTS 备考核心", "用户词书", "csv", builtIn: false);
        var pack = CreatePack(1, 'd',
            new BuiltInVocabularyDeckImport("common", "常见英语词汇", "常见词", "builtin-vocabulary-v1",
                [new WordEntry(0, "apple", null, null, null, null,
                    [new WordSense(0, "n.", "苹果", null, null, null)])]),
            new BuiltInVocabularyDeckImport("ielts", "IELTS 备考核心", "NGSL + NAWL", "builtin-vocabulary-v1",
                [new WordEntry(0, "apple", null, null, null, null,
                    [new WordSense(0, "n.", "苹果", null, null, null)])]));

        var imported = await _db.ImportBuiltInVocabularyAsync(pack);

        Assert.False(imported.AlreadyApplied);
        Assert.Equal(1, imported.WordsAdded);
        Assert.Equal(0, imported.WordsMatched);
        Assert.Equal(2, imported.DecksAdded);
        Assert.Equal(2, imported.DeckWordsAdded);
        Assert.Equal(2, imported.CardsAdded);
        Assert.Equal(1, await _db.CountWordsAsync());
        var decks = await _db.ListDecksAsync();
        Assert.Contains(decks, deck => deck.Name == "IELTS 备考核心" && !deck.BuiltIn && deck.WordCount == 0);
        Assert.Contains(decks, deck => deck.Name == "常见英语词汇" && deck.BuiltIn && deck.WordCount == 1);
        Assert.Contains(decks, deck => deck.Name == "IELTS 备考核心（Lexio 内置）" && deck.BuiltIn && deck.WordCount == 1);
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
    public async Task SearchByDeck_FiltersMatchesAndLoadsDeckContentsForBlankQuery()
    {
        var ieltsDeck = await _db.EnsureDeckAsync("IELTS", null, "test", builtIn: true);
        var universityDeck = await _db.EnsureDeckAsync("University", null, "test", builtIn: true);
        var appleId = await _db.UpsertWordAsync(new(0, "apple", null, null, null, null, []));
        var applyId = await _db.UpsertWordAsync(new(0, "apply", null, null, null, null, []));
        await _db.AddWordToDeckAsync(ieltsDeck, appleId, 0);
        await _db.AddWordToDeckAsync(universityDeck, applyId, 0);

        var matches = await _db.SearchWordsAsync("app", 10, ieltsDeck);
        var deckContents = await _db.SearchWordsAsync("", 10, ieltsDeck);

        Assert.Equal(["apple"], matches.Select(word => word.Lemma));
        Assert.Equal(["apple"], deckContents.Select(word => word.Lemma));
    }

    [Fact]
    public async Task Search_PunctuationDoesNotMergeTokensOrReturnStrippedPrefixMatches()
    {
        await _db.UpsertWordAsync(new(0, "canto", null, null, 1, null, []));
        await _db.UpsertWordAsync(new(0, "can't", null, null, 2, null, []));

        var results = await _db.SearchWordsAsync("can't", 10);

        Assert.Contains(results, word => word.Lemma == "can't");
        Assert.DoesNotContain(results, word => word.Lemma == "canto");
    }

    [Fact]
    public async Task CsvImport_IgnoresBlankRecordsBeforeHeader()
    {
        var csv = Path.Combine(_dir, "leading-blank-records.csv");
        await File.WriteAllTextAsync(csv,
            "\r\n  \r\nword,zh,pos,en\r\nbook,书,n.,a written work\r\n\r\n");

        var result = await _dict.ImportCsvAsync(csv, "Blank Record List");

        Assert.Null(result.Error);
        Assert.Equal(1, result.Added);
        Assert.Equal(0, result.Skipped);
        Assert.Empty(await _db.SearchWordsAsync("word", 3));
        var book = Assert.Single(await _db.SearchWordsAsync("book", 3));
        Assert.Equal("n.", book.Senses.Single().Pos);
        Assert.Equal("书", book.Senses.Single().ZhGloss);
        Assert.Equal("a written work", book.Senses.Single().EnGloss);
    }

    [Theory]
    [InlineData("---")]
    [InlineData("'")]
    [InlineData("-'")]
    public async Task CsvImport_SkipsLemmasWithoutAnyLetters(string lemma)
    {
        var csv = Path.Combine(_dir, "punctuation-only.csv");
        await File.WriteAllTextAsync(csv, $"word,zh\n{lemma},无效词\n");

        var result = await _dict.ImportCsvAsync(csv, "Invalid Lemmas");

        Assert.Equal("没有可导入的有效单词", result.Error);
        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, await _db.CountWordsAsync());
        Assert.Empty(await _db.ListDecksAsync());
    }

    [Fact]
    public async Task CsvImport_BeforeBackupFixtureImportsBothRowsAndSenseFields()
    {
        var csv = Path.Combine(_dir, "before-backup.csv");
        await File.WriteAllTextAsync(csv,
            "word,zh,pos,en,exEn,exZh\n" +
            "serendipity,意外发现的乐趣,n.,a happy accident,The meeting was pure serendipity.,这次相遇纯属意外之喜。\n" +
            "ephemeral,短暂的,adj.,lasting for a short time,The glow was ephemeral.,那道光很短暂。\n");

        var result = await _dict.ImportCsvAsync(csv, "before-backup");

        Assert.Null(result.Error);
        Assert.Equal(2, result.Added);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(2, await _db.CountWordsAsync());
        Assert.Equal("csv", Assert.Single(await _db.ListDecksAsync()).Source);
        var word = (await _db.SearchWordsAsync("serendipity", 3)).Single();
        var sense = Assert.Single(word.Senses);
        Assert.Equal("n.", sense.Pos);
        Assert.Equal("意外发现的乐趣", sense.ZhGloss);
        Assert.Equal("a happy accident", sense.EnGloss);
        Assert.Equal("The meeting was pure serendipity.", sense.ExampleEn);
        Assert.Equal("这次相遇纯属意外之喜。", sense.ExampleZh);
    }

    [Fact]
    public async Task TypeWordsJsonImport_PersistsRichDetailsAndEmptyImportDoesNotClearThem()
    {
        var json = Path.Combine(_dir, "rich-dictionary.json");
        await File.WriteAllTextAsync(json, """
            [{
              "word":"cancel",
              "phonetic0":"ˈkæns(ə)l",
              "phonetic1":"ˈkæns(ə)l",
              "trans":[{"pos":"v.","cn":"取消；终止"}],
              "sentences":[{"c":"The customer called to cancel.","cn":"顾客打电话来取消了。"}],
              "phrases":[{"c":"cancel out","cn":"抵销"}],
              "synos":[{"pos":"v.","cn":"撤销","ws":["withdraw","call off"]}],
              "etymology":[{"t":"词源","d":"来自 cancelli。"}],
              "relWords":{"root":"cancel","rels":[{"pos":"n.","words":[{"c":"cancellation","cn":"取消"}]}]}
            }]
            """);

        var result = await _dict.ImportTypeWordsJsonAsync(json, "TypeWords Rich");

        Assert.Null(result.Error);
        Assert.Equal(1, result.Added);
        Assert.Equal("typewords", Assert.Single(await _db.ListDecksAsync()).Source);
        var word = Assert.Single(await _db.SearchWordsAsync("cancel", 5, deckId: null));
        Assert.Equal("ˈkæns(ə)l", word.IpaUs);
        Assert.Equal("v.", word.Senses[0].Pos);
        Assert.Equal("取消；终止", word.Senses[0].ZhGloss);
        using var savedWordJson = JsonDocument.Parse(JsonSerializer.Serialize(word));
        var details = savedWordJson.RootElement.GetProperty("Details");
        Assert.Equal("The customer called to cancel.", details.GetProperty("Sentences")[0].GetProperty("English").GetString());
        Assert.Equal("抵销", details.GetProperty("Phrases")[0].GetProperty("Chinese").GetString());
        Assert.Equal(new[] { "withdraw", "call off" }, details.GetProperty("Synonyms")[0].GetProperty("Words").EnumerateArray().Select(w => w.GetString()));
        Assert.Equal("来自 cancelli。", details.GetProperty("Etymologies")[0].GetProperty("Description").GetString());
        Assert.Equal("cancellation", details.GetProperty("RelatedGroups")[0].GetProperty("Words")[0].GetProperty("English").GetString());

        var partialJson = Path.Combine(_dir, "partial-rich-dictionary.json");
        await File.WriteAllTextAsync(partialJson, """[{"word":"cancel","phrases":[{"c":"cancel a plan","cn":"取消计划"}]}]""");
        var partialResult = await _dict.ImportTypeWordsJsonAsync(partialJson, "TypeWords Rich");
        Assert.Null(partialResult.Error);
        var afterPartialImport = Assert.Single(await _db.SearchWordsAsync("cancel", 5));
        Assert.Equal(new[] { "cancel out", "cancel a plan" }, afterPartialImport.Details.Phrases.Select(phrase => phrase.English));
        Assert.Single(afterPartialImport.Details.Sentences);
        Assert.Single(afterPartialImport.Details.Synonyms);
        Assert.Single(afterPartialImport.Details.Etymologies);
        Assert.Equal("cancel", afterPartialImport.Details.RelatedRoot);

        var csv = Path.Combine(_dir, "plain-update.csv");
        await File.WriteAllTextAsync(csv, "word,zh\ncancel,取消\n");
        var csvResult = await _dict.ImportCsvAsync(csv, "Plain Update");
        Assert.Null(csvResult.Error);
        var afterPlainImport = JsonDocument.Parse(JsonSerializer.Serialize((await _db.SearchWordsAsync("cancel", 5)).Single()));
        Assert.Single(afterPlainImport.RootElement.GetProperty("Details").GetProperty("Sentences").EnumerateArray());
    }

    [Fact]
    public async Task TypeWordsJsonImport_InvalidFileLeavesNoPartialDeckOrWords()
    {
        var json = Path.Combine(_dir, "broken.json");
        await File.WriteAllTextAsync(json, "[{\"word\":\"valid\"}, {\"word\":");

        var result = await _dict.ImportTypeWordsJsonAsync(json, "Broken TypeWords");

        Assert.NotNull(result.Error);
        Assert.Equal(0, await _db.CountWordsAsync());
        Assert.Empty(await _db.ListDecksAsync());
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
    public async Task CsvImport_HeaderWithoutGlossColumnDoesNotUseMetadataAsZhGloss()
    {
        var csv = Path.Combine(_dir, "ipa-only.csv");
        await File.WriteAllTextAsync(csv, "word,ipaUs\nbook,/bʊk/\n");

        var result = await _dict.ImportCsvAsync(csv, "IPA Only");

        Assert.Null(result.Error);
        Assert.Equal(1, result.Added);
        var book = Assert.Single(await _db.SearchWordsAsync("book", 3));
        Assert.Equal("/bʊk/", book.IpaUs);
        Assert.Null(book.Senses.Single().ZhGloss);
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
    public async Task CsvImport_HeaderlessListCanStartWithTheWordWord()
    {
        var csv = Path.Combine(_dir, "word-first.txt");
        await File.WriteAllTextAsync(csv, "word,词语\nserendipity,意外发现的乐趣\n");

        var result = await _dict.ImportCsvAsync(csv, "Word First List");

        Assert.Null(result.Error);
        Assert.Equal(2, result.Added);
        var imported = Assert.Single(await _db.SearchWordsAsync("word", 3));
        Assert.Equal("词语", imported.Senses.Single().ZhGloss);
    }

    [Fact]
    public async Task CsvImport_PreservesQuotedMultilineFieldsAsOneRecord()
    {
        var csv = Path.Combine(_dir, "multiline.csv");
        await File.WriteAllTextAsync(csv,
            "word,zh,exEn\r\nserendipity,意外发现的乐趣,\"A happy discovery\r\nin a surprising way.\"\r\nephemeral,短暂的,lasting for a short time\r\n");

        var result = await _dict.ImportCsvAsync(csv, "Multiline List");

        Assert.Null(result.Error);
        Assert.Equal(2, result.Added);
        Assert.Equal(2, await _db.CountWordsAsync());
        var word = (await _db.SearchWordsAsync("serendipity", 3)).Single();
        Assert.Equal("A happy discovery\r\nin a surprising way.", word.Senses.Single().ExampleEn);
        Assert.Contains(await _db.SearchWordsAsync("ephemeral", 3), entry => entry.Lemma == "ephemeral");
    }

    [Theory]
    [InlineData("back,front", "意外发现的乐趣,serendipity")]
    [InlineData("front,back", "serendipity,意外发现的乐趣")]
    public async Task CsvImport_MapsFrontAndBackHeadersInAnyColumnOrder(string header, string row)
    {
        var csv = Path.Combine(_dir, "front-back.csv");
        await File.WriteAllTextAsync(csv, $"{header}\n{row}\n");

        var result = await _dict.ImportCsvAsync(csv, "Front Back List");

        Assert.Null(result.Error);
        Assert.Equal(1, result.Added);
        Assert.Equal(1, await _db.CountWordsAsync());
        var word = (await _db.SearchWordsAsync("serendipity", 3)).Single();
        Assert.Equal("意外发现的乐趣", word.Senses.Single().ZhGloss);
    }

    [Fact]
    public async Task CsvImport_DeduplicatesInputAndReportsExistingWordsAsUpdates()
    {
        var csv = Path.Combine(_dir, "duplicates.csv");
        await File.WriteAllTextAsync(csv,
            "word,zh\nserendipity,意外\nSERENDIPITY,重复\nephemeral,短暂\n");

        var first = await _dict.ImportCsvAsync(csv, "Duplicate Deck");
        var second = await _dict.ImportCsvAsync(csv, "Duplicate Deck");

        Assert.Null(first.Error);
        Assert.Equal(2, first.Added);
        Assert.Equal(1, first.Skipped);
        Assert.Equal(0, first.Updated);
        Assert.Null(second.Error);
        Assert.Equal(0, second.Added);
        Assert.Equal(2, second.Updated);
        Assert.Equal(1, second.Skipped);
        Assert.Equal(2, (await _db.ListDecksAsync()).Single().WordCount);
    }

    [Fact]
    public async Task CsvImport_UpdatesOnlySuppliedFieldsAndPreservesOtherSenses()
    {
        long wordId = await _db.UpsertWordAsync(new(0, "book", "/old-us/", "/old-uk/", 42, "B1",
        [
            new(0, "n.", "一本有页面的作品", "a written work", "I read a book.", "我读了一本书。"),
            new(0, "v.", "预订", "reserve", "Book a table.", "预订桌位。"),
        ]));
        var csv = Path.Combine(_dir, "partial-update.csv");
        await File.WriteAllTextAsync(csv, "word,ipaUs,zh\nbook,/new-us/,书籍\n");

        var result = await _dict.ImportCsvAsync(csv, "Partial Update");

        Assert.Null(result.Error);
        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Updated);
        var updated = await _db.GetWordAsync(wordId);
        Assert.NotNull(updated);
        Assert.Equal("/new-us/", updated!.IpaUs);
        Assert.Equal("/old-uk/", updated.IpaUk);
        Assert.Equal(42, updated.FrequencyRank);
        Assert.Equal("B1", updated.Cefr);
        Assert.Equal(new[] { "n.", "v." }, updated.Senses.Select(sense => sense.Pos));
        Assert.Equal("书籍", updated.Senses[0].ZhGloss);
        Assert.Equal("a written work", updated.Senses[0].EnGloss);
        Assert.Equal("I read a book.", updated.Senses[0].ExampleEn);
        Assert.Equal("我读了一本书。", updated.Senses[0].ExampleZh);
        Assert.Equal("预订", updated.Senses[1].ZhGloss);
        Assert.Equal("reserve", updated.Senses[1].EnGloss);

        var emptyCellsCsv = Path.Combine(_dir, "empty-cells.csv");
        await File.WriteAllTextAsync(emptyCellsCsv, "word,ipaUs,zh\nbook,,\n");
        var emptyCellUpdate = await _dict.ImportCsvAsync(emptyCellsCsv, "Partial Update");

        Assert.Null(emptyCellUpdate.Error);
        Assert.Equal(1, emptyCellUpdate.Updated);
        var afterEmptyCells = await _db.GetWordAsync(wordId);
        Assert.Equal("/new-us/", afterEmptyCells!.IpaUs);
        Assert.Equal("书籍", afterEmptyCells.Senses[0].ZhGloss);
    }

    [Fact]
    public async Task CsvImport_BlankHeaderlessGlossPreservesExistingValue()
    {
        var wordId = await _db.UpsertWordAsync(new(0, "book", null, null, null, null,
            [new(0, "n.", "一本书", "a written work", null, null)]));
        var csv = Path.Combine(_dir, "blank-headerless-gloss.csv");
        await File.WriteAllTextAsync(csv, "book,\n");

        var result = await _dict.ImportCsvAsync(csv, "Headerless Update");

        Assert.Null(result.Error);
        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Updated);
        var updated = await _db.GetWordAsync(wordId);
        Assert.Equal("一本书", updated!.Senses.Single().ZhGloss);
        Assert.Equal("a written work", updated.Senses.Single().EnGloss);
    }

    [Fact]
    public async Task CsvImport_MatchesExistingUnicodeLemmaCaseInsensitively()
    {
        var existingId = await _db.UpsertWordAsync(new(0, "Élan", null, null, null, null,
            [new(0, "n.", "气魄", null, null, null)]));
        var csv = Path.Combine(_dir, "unicode-duplicate.csv");
        await File.WriteAllTextAsync(csv, "word,zh\nélan,风格\n");

        var result = await _dict.ImportCsvAsync(csv, "Unicode List");

        Assert.Null(result.Error);
        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Updated);
        Assert.Equal(1, await _db.CountWordsAsync());
        var existing = await _db.GetWordAsync(existingId);
        Assert.Equal("Élan", existing!.Lemma);
        Assert.Equal("风格", existing.Senses.Single().ZhGloss);
    }

    [Fact]
    public async Task CsvImport_MalformedQuotedRowReportsErrorWithoutPartialChanges()
    {
        var csv = Path.Combine(_dir, "malformed.csv");
        await File.WriteAllTextAsync(csv, "word,zh\n\"unfinished,未完成\n");

        var result = await _dict.ImportCsvAsync(csv, "Malformed Deck");

        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Equal(0, result.Added);
        Assert.Equal(0, await _db.CountWordsAsync());
        Assert.Empty(await _db.ListDecksAsync());
    }

    [Fact]
    public async Task CsvImport_FileReadFailureReturnsSafeMessageAndRetainsDiagnosticException()
    {
        var missingPath = Path.Combine(_dir, "missing.csv");

        var result = await _dict.ImportCsvAsync(missingPath, "Missing");

        Assert.Equal("无法读取文件或写入词库。请检查文件访问权限并查看诊断日志。", result.Error);
        Assert.IsType<FileNotFoundException>(result.Failure);
        Assert.DoesNotContain(missingPath, result.Error);
    }

    [Fact]
    public async Task CsvImport_NonUtf8FileExplainsRequiredEncodingWithoutWriting()
    {
        var csv = Path.Combine(_dir, "non-utf8.csv");
        await File.WriteAllBytesAsync(csv,
            [.. System.Text.Encoding.UTF8.GetBytes("word,zh\nbook,"), 0xCA, 0xE9, (byte)'\n']);

        var result = await _dict.ImportCsvAsync(csv, "Non UTF-8 List");

        Assert.Equal("文件必须使用 UTF-8 编码。请另存为 UTF-8 后重试。", result.Error);
        Assert.IsType<System.Text.DecoderFallbackException>(result.Failure);
        Assert.Equal(0, await _db.CountWordsAsync());
        Assert.Empty(await _db.ListDecksAsync());
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


    private static BuiltInVocabularyPack CreatePack(int version, char hashCharacter, params BuiltInVocabularyDeckImport[] decks) =>
        new(version, new string(hashCharacter, 64), decks);

    private async Task<long> ExecuteScalarLongAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_dir, "dict.db"),
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private async Task ExecuteNonQueryAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_dir, "dict.db"),
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
