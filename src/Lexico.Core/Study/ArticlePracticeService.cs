using Lexico.Core.Data;
using Lexico.Core.Domain;

namespace Lexico.Core.Study;

public sealed class ArticlePracticeService(AppDatabase db)
{
    public async Task<ArticlePracticeState> StartAsync(long articleId, ArticlePracticeMode mode)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown article practice mode.");

        var article = await db.GetArticleAsync(articleId).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Article {articleId} does not exist.");
        if (article.Sentences.Count == 0)
            throw new InvalidOperationException("文章没有可练习的句子。");

        var selectedMode = article.Summary.HasProgress ? article.Summary.Mode : mode;
        int current = article.Summary.IsComplete
            ? article.Sentences.Count
            : Math.Clamp(article.Summary.CurrentSentence, 0, article.Sentences.Count - 1);
        if (!article.Summary.HasProgress)
            await db.SaveArticleProgressAsync(articleId, 0, selectedMode, false).ConfigureAwait(false);

        return new ArticlePracticeState(article.Summary, article.Sentences, current, selectedMode);
    }

    public Task SavePositionAsync(long articleId, int currentSentence, ArticlePracticeMode mode, bool isComplete = false) =>
        db.SaveArticleProgressAsync(articleId, currentSentence, mode, isComplete);

    public async Task CompleteAsync(long articleId, ArticlePracticeMode mode)
    {
        var article = await db.GetArticleAsync(articleId).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Article {articleId} does not exist.");
        if (article.Sentences.Count == 0)
            throw new InvalidOperationException("文章没有可完成的句子。");
        await db.SaveArticleProgressAsync(articleId, article.Sentences.Count, mode, true).ConfigureAwait(false);
    }

    public async Task<ArticlePracticeState> RestartAsync(long articleId, ArticlePracticeMode mode)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown article practice mode.");
        var article = await db.GetArticleAsync(articleId).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Article {articleId} does not exist.");
        if (article.Sentences.Count == 0)
            throw new InvalidOperationException("文章没有可重新开始的句子。");
        await db.SaveArticleProgressAsync(articleId, 0, mode, false).ConfigureAwait(false);
        return new ArticlePracticeState(article.Summary with
        {
            CurrentSentence = 0,
            Mode = mode,
            IsComplete = false,
            HasProgress = true,
        }, article.Sentences, 0, mode);
    }
}
