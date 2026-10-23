using Lexico.Core.Data;
using Lexico.Core.Domain;
using Xunit;

namespace Lexico.Core.Tests;

public sealed class ArticlePersistenceTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lexio-article-persistence-" + Guid.NewGuid().ToString("N"));
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
    public async Task NewArticleSaveReturnsAnIdAndPersistsItsSentences()
    {
        var id = await _db.SaveArticleAsync(new ArticleDraft("A New Day",
        [
            new("The sun rose.", "太阳升起。"),
        ]));

        var saved = await _db.GetArticleAsync(id);
        Assert.NotNull(saved);
        Assert.Equal("A New Day", saved!.Summary.Title);
        Assert.Equal("The sun rose.", Assert.Single(saved.Sentences).Text);
    }

    [Fact]
    public async Task MetadataOnlyEditsPreserveArticleSentencesAndPracticeProgress()
    {
        var id = await _db.SaveArticleAsync(new ArticleDraft("A New Day",
        [
            new("The sun rose.", "太阳升起。"),
            new("Birds began to sing.", "鸟儿开始歌唱。"),
        ]));
        await _db.SaveArticleProgressAsync(id, 1, ArticlePracticeMode.FollowWrite, isComplete: false);
        var before = await _db.GetArticleAsync(id);

        await _db.SaveArticleAsync(new ArticleDraft("A New Day", before!.Sentences
            .Select(sentence => new ArticleSentenceDraft(sentence.Text, sentence.Translation)).ToArray())
        {
            Id = id,
            TitleTranslation = "新的一天",
        });

        var after = await _db.GetArticleAsync(id);
        Assert.Equal(before.Sentences.Select(sentence => sentence.Id), after!.Sentences.Select(sentence => sentence.Id));
        Assert.Equal(1, after.Summary.CurrentSentence);
        Assert.False(after.Summary.IsComplete);
        Assert.Equal(ArticlePracticeMode.FollowWrite, after.Summary.Mode);
        Assert.Equal("新的一天", after.Summary.TitleTranslation);

        await _db.SaveArticleProgressAsync(id, 2, ArticlePracticeMode.Dictation, isComplete: true);
        await _db.SaveArticleAsync(new ArticleDraft("A New Day Updated", after.Sentences
            .Select(sentence => new ArticleSentenceDraft(sentence.Text, sentence.Translation)).ToArray())
        {
            Id = id,
            TitleTranslation = "新的一天（修订）",
        });

        var completed = await _db.GetArticleAsync(id);
        Assert.Equal(2, completed!.Summary.CurrentSentence);
        Assert.True(completed.Summary.IsComplete);
        Assert.Equal(ArticlePracticeMode.Dictation, completed.Summary.Mode);
    }

    [Fact]
    public async Task SentenceEditsResetArticleProgressToTheBeginning()
    {
        var id = await _db.SaveArticleAsync(new ArticleDraft("A New Day",
        [
            new("The sun rose.", "太阳升起。"),
            new("Birds began to sing.", "鸟儿开始歌唱。"),
        ]));
        await _db.SaveArticleProgressAsync(id, 1, ArticlePracticeMode.FollowWrite, isComplete: false);
        var before = await _db.GetArticleAsync(id);

        await _db.SaveArticleAsync(new ArticleDraft("A New Day", before!.Sentences
            .Select(sentence => new ArticleSentenceDraft(
                sentence.Position == 0 ? "The sun came up." : sentence.Text,
                sentence.Translation)).ToArray())
        {
            Id = id,
            TitleTranslation = before.Summary.TitleTranslation,
        });

        var after = await _db.GetArticleAsync(id);
        Assert.Equal(0, after!.Summary.CurrentSentence);
        Assert.False(after.Summary.IsComplete);
        Assert.Equal("The sun came up.", after.Sentences[0].Text);
    }
}
