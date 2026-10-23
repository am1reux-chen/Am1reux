using Lexio.Services;
using Lexio.ViewModels;
using Lexico.Core.Data;
using Lexico.Core.Domain;
using Lexico.Core.Scheduling;
using Lexico.Core.Study;
using Xunit;

namespace Lexico.App.Tests;

public sealed class ArticleLearnViewModelTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lexio-article-learn-" + Guid.NewGuid().ToString("N"));
    private AppDatabase _db = null!;
    private ArticleLearnViewModel _viewModel = null!;
    private long _articleId;

    public async Task InitializeAsync()
    {
        _db = new AppDatabase(Path.Combine(_directory, "study.db"));
        await _db.MigrateAsync();
        var article = new ArticleDraft("A New Day",
        [
            new("The sun rose.", "太阳升起。"),
            new("Birds began to sing.", "鸟儿开始歌唱。"),
            new("People opened their windows.", "人们打开窗户。"),
        ])
        {
            TitleTranslation = "新的一天",
        };
        _articleId = await _db.SaveArticleAsync(article);
        var study = new StudyService(_db, new FsrsCoreEngine(enableFuzzing: false));
        _viewModel = new ArticleLearnViewModel(new AppServices(_db, study));
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task SelectingReaderParagraphPersistsPositionAndMovesCurrentHighlight()
    {
        await _viewModel.StartAsync(_articleId, ArticlePracticeMode.FollowWrite);

        Assert.Equal("新的一天", _viewModel.TitleTranslation);
        Assert.Equal(3, _viewModel.ReaderLines.Count);
        Assert.Equal(0, _viewModel.CurrentIndex);
        Assert.Equal(0, _viewModel.CurrentReaderLine!.Position);

        await _viewModel.SelectSentenceAsync(2);

        var saved = await _db.GetArticleAsync(_articleId);
        Assert.Equal(2, saved!.Summary.CurrentSentence);
        Assert.Equal("People opened their windows.", _viewModel.SentenceText);
        Assert.Equal(2, _viewModel.CurrentReaderLine!.Position);

        await _viewModel.SelectSentenceAsync(1);

        saved = await _db.GetArticleAsync(_articleId);
        Assert.Equal(1, saved!.Summary.CurrentSentence);
        Assert.Equal("Birds began to sing.", _viewModel.SentenceText);

        await _viewModel.NextCommand.ExecuteAsync(null);
        await _viewModel.NextCommand.ExecuteAsync(null);
        Assert.True(_viewModel.IsComplete);
        Assert.Null(_viewModel.CurrentReaderLine);

        await _viewModel.SelectSentenceAsync(0);

        saved = await _db.GetArticleAsync(_articleId);
        Assert.False(_viewModel.IsComplete);
        Assert.Equal(0, saved!.Summary.CurrentSentence);
        Assert.False(saved.Summary.IsComplete);
        Assert.Equal(0, _viewModel.CurrentReaderLine!.Position);
    }

    [Fact]
    public async Task CompletedArticleCanSaveANewModeForRestart()
    {
        await _viewModel.StartAsync(_articleId, ArticlePracticeMode.FollowWrite);
        await _viewModel.NextCommand.ExecuteAsync(null);
        await _viewModel.NextCommand.ExecuteAsync(null);
        await _viewModel.NextCommand.ExecuteAsync(null);

        Assert.True(_viewModel.IsComplete);

        await _viewModel.SetModeAsync(ArticlePracticeMode.Dictation);

        Assert.Equal(ArticlePracticeMode.Dictation, _viewModel.Mode);
        var saved = await _db.GetArticleAsync(_articleId);
        Assert.True(saved!.Summary.IsComplete);
        Assert.Equal(ArticlePracticeMode.Dictation, saved.Summary.Mode);

        await _viewModel.RestartCommand.ExecuteAsync(null);

        Assert.False(_viewModel.IsComplete);
        Assert.Equal(ArticlePracticeMode.Dictation, _viewModel.Mode);
        saved = await _db.GetArticleAsync(_articleId);
        Assert.False(saved!.Summary.IsComplete);
        Assert.Equal(ArticlePracticeMode.Dictation, saved.Summary.Mode);
    }
}
