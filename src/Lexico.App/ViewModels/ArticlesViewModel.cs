using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lexio.Services;
using Lexico.Core.Domain;
using Lexico.Core.Study;

namespace Lexio.ViewModels;

public sealed record ArticlePracticeModeChoice(ArticlePracticeMode Mode, string Name);

public sealed partial class ArticleEditorSentence(string text, string? translation) : ObservableObject
{
    [ObservableProperty] private string text = text;
    [ObservableProperty] private string translation = translation ?? "";
}

public sealed partial class ArticlesViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Action<Exception>? _reportError;

    [ObservableProperty] private string title = "";
    [ObservableProperty] private string titleTranslation = "";
    [ObservableProperty] private string sentenceDraftText = "";
    [ObservableProperty] private string statusText = "";
    [ObservableProperty] private long? editingArticleId;
    [ObservableProperty] private ArticlePracticeMode selectedPracticeMode = ArticlePracticeMode.FollowWrite;
    [ObservableProperty] private bool loading;
    [ObservableProperty] private ArticleSummary? selectedArticle;

    public bool HasSelectedArticle => SelectedArticle is not null;
    public bool CanPractice => HasSelectedArticle && !Loading;

    public ObservableCollection<ArticleSummary> Articles { get; } = [];
    public ObservableCollection<ArticleEditorSentence> EditorSentences { get; } = [];
    public IReadOnlyList<ArticlePracticeModeChoice> PracticeModes { get; } =
    [
        new(ArticlePracticeMode.FollowWrite, "跟写"),
        new(ArticlePracticeMode.Dictation, "听写"),
    ];

    public IAsyncRelayCommand SaveArticleCommand { get; }
    public IAsyncRelayCommand<ArticleSummary> DeleteArticleCommand { get; }
    public IAsyncRelayCommand<string> ImportCommand { get; }
    public IAsyncRelayCommand<ArticleSummary> PracticeCommand { get; }
    public IRelayCommand NewArticleCommand { get; }

    public event Action<ArticlePracticeRequest>? PracticeRequested;

    partial void OnLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanPractice));
        SaveArticleCommand.NotifyCanExecuteChanged();
        DeleteArticleCommand.NotifyCanExecuteChanged();
        ImportCommand.NotifyCanExecuteChanged();
        PracticeCommand.NotifyCanExecuteChanged();
        NewArticleCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedArticleChanged(ArticleSummary? value)
    {
        OnPropertyChanged(nameof(HasSelectedArticle));
        OnPropertyChanged(nameof(CanPractice));
        PracticeCommand.NotifyCanExecuteChanged();
        DeleteArticleCommand.NotifyCanExecuteChanged();
    }

    public ArticlesViewModel(AppServices services, Action<Exception>? reportError = null)
    {
        _services = services;
        _reportError = reportError;
        SaveArticleCommand = new AsyncRelayCommand(SaveArticleAsync, () => !Loading);
        DeleteArticleCommand = new AsyncRelayCommand<ArticleSummary>(DeleteArticleAsync, _ => !Loading);
        ImportCommand = new AsyncRelayCommand<string>(ImportAsync, _ => !Loading);
        PracticeCommand = new AsyncRelayCommand<ArticleSummary>(PracticeAsync, _ => !Loading);
        NewArticleCommand = new RelayCommand(NewArticle, () => !Loading);
    }

    public async Task LoadAsync()
    {
        try
        {
            await _services.DispatchAsync(() => Loading = true).ConfigureAwait(false);
            await RefreshArticlesAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _reportError?.Invoke(error);
            await _services.DispatchAsync(() => StatusText = "文章列表加载失败，请重试。").ConfigureAwait(false);
        }
        finally
        {
            await _services.DispatchAsync(() => Loading = false).ConfigureAwait(false);
        }
    }

    public async Task SelectArticleAsync(ArticleSummary? article)
    {
        if (article is null)
            return;
        try
        {
            await _services.DispatchAsync(() => Loading = true).ConfigureAwait(false);
            var details = await _services.Db.GetArticleAsync(article.Id).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("文章已被删除。");
            await _services.DispatchAsync(() =>
            {
                EditingArticleId = article.Id;
                SelectedArticle = Articles.FirstOrDefault(item => item.Id == article.Id) ?? details.Summary;
                Title = details.Summary.Title;
                TitleTranslation = details.Summary.TitleTranslation ?? "";
                EditorSentences.Clear();
                foreach (var sentence in details.Sentences)
                    EditorSentences.Add(new ArticleEditorSentence(sentence.Text, sentence.Translation));
                SentenceDraftText = "";
                StatusText = "";
            }).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _reportError?.Invoke(error);
            await _services.DispatchAsync(() => StatusText = "文章无法打开，请刷新后重试。").ConfigureAwait(false);
        }
        finally
        {
            await _services.DispatchAsync(() => Loading = false).ConfigureAwait(false);
        }
    }

    public void NewArticle()
    {
        EditingArticleId = null;
        SelectedArticle = null;
        Title = "";
        TitleTranslation = "";
        SentenceDraftText = "";
        EditorSentences.Clear();
        StatusText = "新文章尚未保存。";
    }

    public void AddSentenceLines()
    {
        var lines = SplitLines(SentenceDraftText);
        foreach (var line in lines)
            EditorSentences.Add(new ArticleEditorSentence(line, null));
        SentenceDraftText = "";
        StatusText = lines.Count == 0 ? "请输入至少一句非空文本。" : $"已添加 {lines.Count} 句。";
    }

    public void RemoveSentence(ArticleEditorSentence? sentence)
    {
        if (sentence is not null)
            EditorSentences.Remove(sentence);
    }

    private async Task SaveArticleAsync()
    {
        var pendingLines = SplitLines(SentenceDraftText);
        var sentences = EditorSentences
            .Select(sentence => new ArticleSentenceDraft(sentence.Text, sentence.Translation))
            .Concat(pendingLines.Select(line => new ArticleSentenceDraft(line, null)))
            .ToArray();
        if (string.IsNullOrWhiteSpace(Title))
        {
            StatusText = "请填写文章标题。";
            return;
        }
        if (sentences.Length == 0 || sentences.Any(sentence => string.IsNullOrWhiteSpace(sentence.Text)))
        {
            StatusText = "请至少添加一句非空句子。";
            return;
        }

        try
        {
            Loading = true;
            var id = await _services.Db.SaveArticleAsync(new ArticleDraft(Title, sentences)
                { Id = EditingArticleId, TitleTranslation = TitleTranslation })
                .ConfigureAwait(false);
            await RefreshArticlesAsync().ConfigureAwait(false);
            ArticleSummary? savedArticle = null;
            await _services.DispatchAsync(() => savedArticle = Articles.FirstOrDefault(article => article.Id == id))
                .ConfigureAwait(false);
            await SelectArticleAsync(savedArticle).ConfigureAwait(false);
            await _services.DispatchAsync(() => StatusText = "文章已保存。修改句子内容会将练习进度重置到第一句；仅修改标题或标题译文会保留进度。")
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _reportError?.Invoke(error);
            await _services.DispatchAsync(() => StatusText = $"文章保存失败：{error.Message}").ConfigureAwait(false);
        }
        finally
        {
            await _services.DispatchAsync(() => Loading = false).ConfigureAwait(false);
        }
    }

    private async Task ImportAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            Loading = true;
            string extension = Path.GetExtension(path);
            if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                var importer = new TypeWordsArticleImporter(_services.Db);
                var result = extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
                    ? await importer.ImportArchiveAsync(path).ConfigureAwait(false)
                    : await importer.ImportAsync(path).ConfigureAwait(false);
                if (result.Error is not null)
                {
                    if (result.Failure is not null)
                        _reportError?.Invoke(result.Failure);
                    await _services.DispatchAsync(() => StatusText = result.Error).ConfigureAwait(false);
                    return;
                }

                await RefreshArticlesAsync().ConfigureAwait(false);
                if (result.ArticleIds.Count != 0)
                {
                    long jsonArticleId = result.ArticleIds[^1];
                    ArticleSummary? jsonImportedArticle = null;
                    await _services.DispatchAsync(() => jsonImportedArticle = Articles.FirstOrDefault(article => article.Id == jsonArticleId))
                        .ConfigureAwait(false);
                    await SelectArticleAsync(jsonImportedArticle).ConfigureAwait(false);
                }
                string status = result.Added == 0
                    ? $"{result.CollectionName ?? "文章"}：跳过 {result.Skipped} 篇重复文章。"
                    : $"{result.CollectionName ?? "文章"}：已导入 {result.Added} 篇，跳过 {result.Skipped} 篇重复文章。";
                if (result.ExpectedCount is { } expected && result.SourceCount is { } actual && expected != actual)
                    status += $"ZIP 清单标记 {expected} 篇，文件实际 {actual} 篇。";
                await _services.DispatchAsync(() => StatusText = status)
                    .ConfigureAwait(false);
                return;
            }

            byte[] bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            string text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes)
                .TrimStart('\uFEFF');
            var lines = SplitLines(text);
            if (lines.Count == 0)
            {
                await _services.DispatchAsync(() => StatusText = "导入文本没有非空句子，未创建文章。")
                    .ConfigureAwait(false);
                return;
            }

            string title = Path.GetFileNameWithoutExtension(path).Trim();
            if (title.Length == 0)
                title = "导入文章";
            long id = await _services.Db.SaveArticleAsync(new ArticleDraft(title,
                lines.Select(line => new ArticleSentenceDraft(line, null)).ToArray())).ConfigureAwait(false);
            await RefreshArticlesAsync().ConfigureAwait(false);
            ArticleSummary? importedArticle = null;
            await _services.DispatchAsync(() => importedArticle = Articles.FirstOrDefault(article => article.Id == id))
                .ConfigureAwait(false);
            await SelectArticleAsync(importedArticle).ConfigureAwait(false);
            await _services.DispatchAsync(() => StatusText = $"已导入 {lines.Count} 句。")
                .ConfigureAwait(false);
        }
        catch (DecoderFallbackException error)
        {
            _reportError?.Invoke(error);
            await _services.DispatchAsync(() => StatusText = "文件不是有效 UTF-8 文本，未创建文章。请选择 UTF-8 编码文件。")
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _reportError?.Invoke(error);
            await _services.DispatchAsync(() => StatusText = $"文章导入失败：{error.Message}").ConfigureAwait(false);
        }
        finally
        {
            await _services.DispatchAsync(() => Loading = false).ConfigureAwait(false);
        }
    }

    private async Task DeleteArticleAsync(ArticleSummary? article)
    {
        if (article is null)
            return;
        try
        {
            Loading = true;
            await _services.Db.DeleteArticleAsync(article.Id).ConfigureAwait(false);
            await _services.DispatchAsync(() =>
            {
                if (EditingArticleId == article.Id)
                    NewArticle();
            }).ConfigureAwait(false);
            await RefreshArticlesAsync().ConfigureAwait(false);
            await _services.DispatchAsync(() => StatusText = "文章和逐句进度已删除。").ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _reportError?.Invoke(error);
            await _services.DispatchAsync(() => StatusText = "文章删除失败，请重试。").ConfigureAwait(false);
        }
        finally
        {
            await _services.DispatchAsync(() => Loading = false).ConfigureAwait(false);
        }
    }

    private Task PracticeAsync(ArticleSummary? article)
    {
        if (article is null)
            return Task.CompletedTask;
        PracticeRequested?.Invoke(new ArticlePracticeRequest(article.Id, SelectedPracticeMode));
        return Task.CompletedTask;
    }

    private async Task RefreshArticlesAsync()
    {
        var articles = await _services.Db.ListArticlesAsync().ConfigureAwait(false);
        await _services.DispatchAsync(() =>
        {
            Articles.Clear();
            foreach (var article in articles)
                Articles.Add(article);
            if (articles.Count == 0 && string.IsNullOrWhiteSpace(StatusText))
                StatusText = "还没有文章。可以新建文章或导入 UTF-8 文本。";
        }).ConfigureAwait(false);
    }

    private static List<string> SplitLines(string text) => text
        .Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(line => line.Length > 0)
        .ToList();
}
