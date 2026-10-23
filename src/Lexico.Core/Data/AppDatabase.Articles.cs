using System.Globalization;
using System.Text.Json;
using Lexico.Core.Domain;
using Microsoft.Data.Sqlite;

namespace Lexico.Core.Data;

public sealed record ArticleImportPersistenceResult(int Added, int Skipped, IReadOnlyList<long> ArticleIds);

public sealed partial class AppDatabase
{
    public Task<List<ArticleSummary>> ListArticlesAsync() => RunAsync(() =>
    {
        var articles = new List<ArticleSummary>();
        using var command = Cmd(ArticleSummarySql + " ORDER BY COALESCE(p.updated_at, a.updated_at) DESC, a.id DESC");
        using var reader = command.ExecuteReader();
        while (reader.Read())
            articles.Add(ReadArticleSummary(reader));
        return articles;
    });

    public Task<ArticleDetails?> GetArticleAsync(long id) => RunAsync(() =>
    {
        ArticleSummary? summary;
        using (var command = Cmd(ArticleSummarySql + " WHERE a.id=@id", ("@id", id)))
        using (var reader = command.ExecuteReader())
            summary = reader.Read() ? ReadArticleSummary(reader) : null;
        if (summary is null)
            return null;

        var sentences = new List<ArticleSentence>(summary.SentenceCount);
        using (var command = Cmd("""
            SELECT id, article_id, position, text, translation
            FROM article_sentences
            WHERE article_id=@id
            ORDER BY position
            """, ("@id", id)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                sentences.Add(new ArticleSentence(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2),
                    reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }
        return new ArticleDetails(summary, sentences);
    });

    public Task<long> SaveArticleAsync(ArticleDraft article)
    {
        ArgumentNullException.ThrowIfNull(article);
        ArgumentNullException.ThrowIfNull(article.Sentences);
        string title = article.Title?.Trim() ?? string.Empty;
        if (title.Length == 0)
            throw new ArgumentException("文章标题不能为空。", nameof(article));
        string? titleTranslation = NullIfWhiteSpace(article.TitleTranslation);
        if (article.Sentences.Count == 0 || article.Sentences.Any(sentence => string.IsNullOrWhiteSpace(sentence.Text)))
            throw new ArgumentException("文章至少需要一句非空句子。", nameof(article));
        var sentences = article.Sentences
            .Select(sentence => new ArticleSentenceDraft(sentence.Text.Trim(), NullIfWhiteSpace(sentence.Translation)))
            .ToArray();

        return RunAsync(() =>
        {
            DateTime now = DateTime.UtcNow;
            string timestamp = Fmt(now);
            using var transaction = _conn.BeginTransaction();
            long articleId;
            bool replaceSentences = true;
            if (article.Id is { } existingId)
            {
                using var update = Cmd("UPDATE articles SET title=@title, title_translation=@translation, updated_at=@updated WHERE id=@id",
                    ("@title", title), ("@translation", titleTranslation), ("@updated", timestamp), ("@id", existingId));
                update.Transaction = transaction;
                if (update.ExecuteNonQuery() == 0)
                    throw new KeyNotFoundException($"Article {existingId} does not exist.");
                articleId = existingId;

                replaceSentences = !ArticleSentencesMatch(articleId, sentences, transaction);
                if (replaceSentences)
                {
                    using (var delete = Cmd("DELETE FROM article_sentences WHERE article_id=@id", ("@id", articleId)))
                    {
                        delete.Transaction = transaction;
                        delete.ExecuteNonQuery();
                    }
                    using var reset = Cmd("UPDATE article_progress SET current_sentence=0, is_complete=0, updated_at=@updated WHERE article_id=@id",
                        ("@updated", timestamp), ("@id", articleId));
                    reset.Transaction = transaction;
                    reset.ExecuteNonQuery();
                }
            }
            else
            {
                using var insert = Cmd("INSERT INTO articles(title, title_translation, created_at, updated_at) VALUES(@title, @translation, @created, @updated)",
                    ("@title", title), ("@translation", titleTranslation), ("@created", timestamp), ("@updated", timestamp));
                insert.Transaction = transaction;
                insert.ExecuteNonQuery();
                using var id = Cmd("SELECT last_insert_rowid()");
                id.Transaction = transaction;
                articleId = (long)id.ExecuteScalar()!;
            }

            for (int position = 0; replaceSentences && position < sentences.Length; position++)
            {
                using var insertSentence = Cmd("""
                    INSERT INTO article_sentences(article_id, position, text, translation)
                    VALUES(@article, @position, @text, @translation)
                    """, ("@article", articleId), ("@position", position),
                    ("@text", sentences[position].Text), ("@translation", sentences[position].Translation));
                insertSentence.Transaction = transaction;
                insertSentence.ExecuteNonQuery();
            }

            using (var update = Cmd("UPDATE articles SET updated_at=@updated WHERE id=@id", ("@updated", timestamp), ("@id", articleId)))
            {
                update.Transaction = transaction;
                update.ExecuteNonQuery();
            }
            transaction.Commit();
            return articleId;
        });
    }

    private bool ArticleSentencesMatch(long articleId, IReadOnlyList<ArticleSentenceDraft> sentences, SqliteTransaction transaction)
    {
        using var command = Cmd("SELECT text, translation FROM article_sentences WHERE article_id=@id ORDER BY position", ("@id", articleId));
        command.Transaction = transaction;
        using var reader = command.ExecuteReader();
        int position = 0;
        while (reader.Read())
        {
            if (position >= sentences.Count ||
                reader.GetString(0) != sentences[position].Text ||
                (reader.IsDBNull(1) ? null : reader.GetString(1)) != sentences[position].Translation)
                return false;
            position++;
        }
        return position == sentences.Count;
    }

    /// <summary>Import a validated article batch atomically, skipping exact title and bilingual-content duplicates.</summary>
    public Task<ArticleImportPersistenceResult> ImportArticlesAsync(IReadOnlyList<ArticleDraft> articles)
    {
        ArgumentNullException.ThrowIfNull(articles);
        if (articles.Count == 0)
            throw new ArgumentException("文章导入列表不能为空。", nameof(articles));

        var drafts = articles.Select(NormalizeArticleDraft).ToArray();
        return RunAsync(() =>
        {
            using var transaction = _conn.BeginTransaction();
            var contentKeys = ReadArticleContentKeys(transaction);
            var articleIds = new List<long>(drafts.Length);
            int skipped = 0;
            var timestamp = Fmt(DateTime.UtcNow);

            foreach (var draft in drafts)
            {
                string key = ArticleContentKey(draft.Title, draft.TitleTranslation, draft.Sentences);
                if (!contentKeys.Add(key))
                {
                    skipped++;
                    continue;
                }

                using var insert = Cmd("INSERT INTO articles(title, title_translation, created_at, updated_at) VALUES(@title, @translation, @created, @updated)");
                insert.Transaction = transaction;
                insert.Parameters.AddWithValue("@title", draft.Title);
                insert.Parameters.AddWithValue("@translation", (object?)draft.TitleTranslation ?? DBNull.Value);
                insert.Parameters.AddWithValue("@created", timestamp);
                insert.Parameters.AddWithValue("@updated", timestamp);
                insert.ExecuteNonQuery();

                using var id = Cmd("SELECT last_insert_rowid()");
                id.Transaction = transaction;
                long articleId = (long)id.ExecuteScalar()!;
                articleIds.Add(articleId);

                for (int position = 0; position < draft.Sentences.Count; position++)
                {
                    var sentence = draft.Sentences[position];
                    using var insertSentence = Cmd("INSERT INTO article_sentences(article_id, position, text, translation) VALUES(@article, @position, @text, @translation)");
                    insertSentence.Transaction = transaction;
                    insertSentence.Parameters.AddWithValue("@article", articleId);
                    insertSentence.Parameters.AddWithValue("@position", position);
                    insertSentence.Parameters.AddWithValue("@text", sentence.Text);
                    insertSentence.Parameters.AddWithValue("@translation", (object?)sentence.Translation ?? DBNull.Value);
                    insertSentence.ExecuteNonQuery();
                }
            }

            transaction.Commit();
            return new ArticleImportPersistenceResult(articleIds.Count, skipped, articleIds);
        });
    }

    public Task DeleteArticleAsync(long id) => RunAsync(() =>
    {
        using var transaction = _conn.BeginTransaction();
        using var command = Cmd("DELETE FROM articles WHERE id=@id", ("@id", id));
        command.Transaction = transaction;
        if (command.ExecuteNonQuery() == 0)
            throw new KeyNotFoundException($"Article {id} does not exist.");
        transaction.Commit();
    });

    public Task SaveArticleProgressAsync(long articleId, int currentSentence, ArticlePracticeMode mode, bool isComplete) => RunAsync(() =>
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown article practice mode.");
        if (currentSentence < 0)
            throw new ArgumentOutOfRangeException(nameof(currentSentence));

        using var transaction = _conn.BeginTransaction();
        int sentenceCount;
        using (var count = Cmd("""
            SELECT COUNT(*), EXISTS(SELECT 1 FROM articles WHERE id=@id)
            FROM article_sentences WHERE article_id=@id
            """, ("@id", articleId)))
        {
            count.Transaction = transaction;
            using var reader = count.ExecuteReader();
            if (!reader.Read() || reader.GetInt32(1) == 0)
                throw new KeyNotFoundException($"Article {articleId} does not exist.");
            sentenceCount = reader.GetInt32(0);
        }
        if (sentenceCount == 0)
            throw new InvalidOperationException("文章没有可保存的句子。");

        int position = isComplete
            ? sentenceCount
            : Math.Clamp(currentSentence, 0, sentenceCount - 1);
        string timestamp = Fmt(DateTime.UtcNow);
        using (var save = Cmd("""
            INSERT INTO article_progress(article_id, current_sentence, mode, is_complete, updated_at)
            VALUES(@id, @position, @mode, @complete, @updated)
            ON CONFLICT(article_id) DO UPDATE SET
                current_sentence=excluded.current_sentence,
                mode=excluded.mode,
                is_complete=excluded.is_complete,
                updated_at=excluded.updated_at
            """, ("@id", articleId), ("@position", position), ("@mode", (int)mode),
            ("@complete", isComplete ? 1 : 0), ("@updated", timestamp)))
        {
            save.Transaction = transaction;
            save.ExecuteNonQuery();
        }
        using (var update = Cmd("UPDATE articles SET updated_at=@updated WHERE id=@id", ("@updated", timestamp), ("@id", articleId)))
        {
            update.Transaction = transaction;
            update.ExecuteNonQuery();
        }
        transaction.Commit();
    });

    private const string ArticleSummarySql = """
        SELECT a.id, a.title,
               (SELECT COUNT(*) FROM article_sentences s WHERE s.article_id=a.id),
               COALESCE(p.current_sentence, 0), COALESCE(p.mode, 0), COALESCE(p.is_complete, 0),
               COALESCE(p.updated_at, a.updated_at), p.article_id IS NOT NULL, a.title_translation
        FROM articles a
        LEFT JOIN article_progress p ON p.article_id=a.id
        """;

    private static ArticleSummary ReadArticleSummary(SqliteDataReader reader)
    {
        int modeValue = reader.GetInt32(4);
        if (!Enum.IsDefined((ArticlePracticeMode)modeValue))
            throw new InvalidDataException($"Article practice mode {modeValue} is invalid.");
        return new ArticleSummary(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3),
            (ArticlePracticeMode)modeValue, reader.GetInt32(5) != 0, ParseUtc(reader.GetString(6)))
        {
            HasProgress = reader.GetInt32(7) != 0,
            TitleTranslation = reader.IsDBNull(8) ? null : reader.GetString(8),
        };
    }

    private static ArticleDraft NormalizeArticleDraft(ArticleDraft article)
    {
        ArgumentNullException.ThrowIfNull(article);
        ArgumentNullException.ThrowIfNull(article.Sentences);
        string title = article.Title?.Trim() ?? string.Empty;
        if (title.Length == 0)
            throw new ArgumentException("文章标题不能为空。", nameof(article));
        if (article.Sentences.Count == 0 || article.Sentences.Any(sentence => string.IsNullOrWhiteSpace(sentence.Text)))
            throw new ArgumentException("文章至少需要一句非空句子。", nameof(article));
        return new ArticleDraft(title, article.Sentences.Select(sentence => new ArticleSentenceDraft(
            sentence.Text.Trim(), NullIfWhiteSpace(sentence.Translation))).ToArray())
        {
            TitleTranslation = NullIfWhiteSpace(article.TitleTranslation),
        };
    }

    private HashSet<string> ReadArticleContentKeys(SqliteTransaction transaction)
    {
        var contentKeys = new HashSet<string>(StringComparer.Ordinal);
        using var command = Cmd("""
            SELECT a.id, a.title, a.title_translation, s.text, s.translation
            FROM articles a LEFT JOIN article_sentences s ON s.article_id=a.id
            ORDER BY a.id, s.position
            """);
        command.Transaction = transaction;
        using var reader = command.ExecuteReader();
        long currentId = 0;
        string? title = null;
        string? titleTranslation = null;
        var sentences = new List<ArticleSentenceDraft>();
        while (reader.Read())
        {
            long id = reader.GetInt64(0);
            if (currentId != id)
            {
                if (currentId != 0)
                    contentKeys.Add(ArticleContentKey(title!, titleTranslation, sentences));
                currentId = id;
                title = reader.GetString(1);
                titleTranslation = reader.IsDBNull(2) ? null : reader.GetString(2);
                sentences.Clear();
            }
            if (!reader.IsDBNull(3))
                sentences.Add(new ArticleSentenceDraft(reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        if (currentId != 0)
            contentKeys.Add(ArticleContentKey(title!, titleTranslation, sentences));
        return contentKeys;
    }

    private static string ArticleContentKey(string title, string? titleTranslation, IReadOnlyList<ArticleSentenceDraft> sentences) =>
        JsonSerializer.Serialize(new
        {
            Title = title.Trim(),
            TitleTranslation = NullIfWhiteSpace(titleTranslation),
            Sentences = sentences.Select(sentence => new
            {
                Text = sentence.Text.Trim(),
                Translation = NullIfWhiteSpace(sentence.Translation),
            }).ToArray(),
        });

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
