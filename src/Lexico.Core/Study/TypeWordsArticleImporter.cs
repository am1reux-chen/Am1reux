using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lexico.Core.Data;
using Lexico.Core.Domain;

namespace Lexico.Core.Study;

public sealed record TypeWordsArticleImportResult(
    int Added,
    int Skipped,
    IReadOnlyList<long> ArticleIds,
    string? Error)
{
    public Exception? Failure { get; init; }
    public string? CollectionName { get; init; }
    public int? SourceCount { get; init; }
    public int? ExpectedCount { get; init; }
}

/// <summary>Imports user-selected TypeWords article JSON without relying on its remote audio URLs.</summary>
public sealed class TypeWordsArticleImporter(AppDatabase db)
{
    public async Task<TypeWordsArticleImportResult> ImportAsync(string path)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            var json = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
            return await ImportJsonContentAsync(json).ConfigureAwait(false);
        }
        catch (DecoderFallbackException error)
        {
            return Failure("文件必须使用 UTF-8 编码。请另存为 UTF-8 后重试。", error);
        }
        catch (Exception error)
        {
            return Failure("文章导入失败，未写入不完整内容。请检查文件后重试。", error);
        }
    }

    public async Task<TypeWordsArticleImportResult> ImportArchiveAsync(string path)
    {
        try
        {
            var content = await TypeWordsArchiveReader.ReadRecommendedJsonAsync(path, TypeWordsContentKind.Article)
                .ConfigureAwait(false);
            var result = await ImportJsonContentAsync(content.Json).ConfigureAwait(false);
            return result with { CollectionName = content.Name, ExpectedCount = content.ExpectedCount };
        }
        catch (Exception error)
        {
            return Failure("无法读取 TypeWords ZIP；请确认它包含推荐文章清单和对应双语 JSON，且仅有一份推荐文章书。", error);
        }
    }

    private async Task<TypeWordsArticleImportResult> ImportJsonContentAsync(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return Failure("TypeWords 文章必须是 JSON 数组。", null);

            int sourceCount = document.RootElement.GetArrayLength();
            var drafts = new List<ArticleDraft>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    return Failure("文章数组包含无效条目，未写入任何文章。", null);

                string? title = ReadString(item, "title")?.Trim();
                string? english = ReadString(item, "text");
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(english))
                    return Failure("每篇文章都需要标题和英文正文；未写入任何文章。", null);

                var paragraphs = SplitParagraphs(english);
                if (paragraphs.Length == 0)
                    return Failure($"文章“{title}”没有可导入的英文段落；未写入任何文章。", null);

                var translations = SplitParagraphs(ReadString(item, "textTranslate") ?? "");
                if (translations.Length != paragraphs.Length)
                    return Failure($"文章“{title}”的英文与译文段落数量不一致；未写入任何文章。", null);

                var sentences = paragraphs.Select((paragraph, index) => new ArticleSentenceDraft(
                    paragraph,
                    translations[index])).ToArray();
                drafts.Add(new ArticleDraft(title, sentences)
                {
                    TitleTranslation = ReadString(item, "titleTranslate"),
                });
            }

            if (drafts.Count == 0)
                return Failure("文件中没有文章，未写入任何内容。", null);

            var imported = await db.ImportArticlesAsync(drafts).ConfigureAwait(false);
            return new TypeWordsArticleImportResult(imported.Added, imported.Skipped, imported.ArticleIds, null)
            { SourceCount = sourceCount };
        }
        catch (JsonException error)
        {
            return Failure("无法读取 TypeWords 文章 JSON；请确认文件格式正确，未写入文章。", error);
        }
        catch (Exception error)
        {
            return Failure("文章导入失败，未写入不完整内容。请检查文件后重试。", error);
        }
    }

    private static string? ReadString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string[] SplitParagraphs(string text) => Regex.Split(text.Trim(), @"\r?\n[\t ]*\r?\n")
        .Select(paragraph => paragraph.Trim())
        .Where(paragraph => paragraph.Length != 0)
        .ToArray();

    private static TypeWordsArticleImportResult Failure(string message, Exception? error) =>
        new(0, 0, [], message) { Failure = error };
}
