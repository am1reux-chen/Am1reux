namespace Lexico.Core.Domain;

public enum StudyMode
{
    Recall,
    FollowWrite,
    SpellFromMeaning,
    Dictation,
    SelfTest,
}

public enum CardMark
{
    Favorite,
    Wrong,
    Mastered,
}

public enum ArticlePracticeMode
{
    FollowWrite,
    Dictation,
}

public sealed record ArticleSummary(
    long Id,
    string Title,
    int SentenceCount,
    int CurrentSentence,
    ArticlePracticeMode Mode,
    bool IsComplete,
    DateTime UpdatedAtUtc)
{
    public bool HasProgress { get; init; }
    public string? TitleTranslation { get; init; }
    public bool HasTitleTranslation => !string.IsNullOrWhiteSpace(TitleTranslation);
    public string ProgressText => IsComplete
        ? $"已完成 · {SentenceCount} 句"
        : CurrentSentence == 0
            ? $"未开始 · {SentenceCount} 句"
            : $"继续第 {CurrentSentence + 1} 句 · 共 {SentenceCount} 句";
}

public sealed record ArticleSentence(long Id, long ArticleId, int Position, string Text, string? Translation);

public sealed record ArticleSentenceDraft(string Text, string? Translation);

public sealed record ArticleDraft(string Title, IReadOnlyList<ArticleSentenceDraft> Sentences)
{
    public long? Id { get; init; }
    public string? TitleTranslation { get; init; }
}

public sealed record ArticleDetails(ArticleSummary Summary, IReadOnlyList<ArticleSentence> Sentences);

public sealed record ArticlePracticeState(
    ArticleSummary Article,
    IReadOnlyList<ArticleSentence> Sentences,
    int CurrentSentence,
    ArticlePracticeMode Mode);

public sealed record ArticlePracticeRequest(long ArticleId, ArticlePracticeMode Mode);

public sealed record WordSense(long Id, string? Pos, string? ZhGloss, string? EnGloss, string? ExampleEn, string? ExampleZh);

public sealed record WordText(string English, string Chinese);

public sealed record WordSynonym(string? Pos, string? Gloss, IReadOnlyList<string> Words);

public sealed record WordEtymology(string Title, string Description);

public sealed record WordRelatedGroup(string? Pos, IReadOnlyList<WordText> Words);

public sealed record WordDetails(
    IReadOnlyList<WordText> Sentences,
    IReadOnlyList<WordText> Phrases,
    IReadOnlyList<WordSynonym> Synonyms,
    IReadOnlyList<WordEtymology> Etymologies,
    string? RelatedRoot,
    IReadOnlyList<WordRelatedGroup> RelatedGroups)
{
    public static WordDetails Empty { get; } = new([], [], [], [], null, []);

    public bool HasContent => Sentences.Count != 0 || Phrases.Count != 0 || Synonyms.Count != 0 ||
        Etymologies.Count != 0 || !string.IsNullOrWhiteSpace(RelatedRoot) || RelatedGroups.Count != 0;
}

public sealed record MarkedWordEntry(
    long CardId,
    long DeckId,
    string DeckName,
    WordEntry WordEntry,
    bool IsFavorite,
    bool IsWrong,
    bool IsMastered);

public sealed record LibraryCardEntry(
    long CardId,
    long DeckId,
    string DeckName,
    WordEntry WordEntry,
    bool IsFavorite,
    bool IsWrong,
    bool IsMastered);

public sealed record CardMarkState(bool IsFavorite, bool IsWrong, bool IsMastered);

public sealed record WordEntry(
    long Id,
    string Lemma,
    string? IpaUs,
    string? IpaUk,
    int? FrequencyRank,
    string? Cefr,
    IReadOnlyList<WordSense> Senses)
{
    public WordDetails Details { get; init; } = WordDetails.Empty;
    public override string ToString() => Lemma;
}

public sealed record DeckInfo(long Id, string Name, string? Description, string? Source, bool BuiltIn, int WordCount);

/// <summary>A card's persisted scheduling state (our storage model, engine-agnostic).</summary>
public sealed record CardRecord(
    long Id,
    long WordId,
    long DeckId,
    CardState State,
    int? Step,
    double? Stability,
    double? Difficulty,
    DateTime DueUtc,
    DateTime? LastReviewUtc,
    int Reps,
    int Lapses,
    DateTime CreatedAtUtc);

/// <summary>Immutable review history row (source of truth for analytics and model training).</summary>
public sealed record ReviewLogRow(
    long Id,
    long CardId,
    DateTime ReviewedAtUtc,
    Grade Rating,
    CardState StateBefore,
    int? StepBefore,
    double? StabilityBefore,
    double? DifficultyBefore,
    double? ScheduledDaysBefore,
    int RepsBefore,
    double RawElapsedMs,
    double EffectiveElapsedMs,
    bool TimingValid,
    double? FsrsRetrievability,
    string EngineVersion,
    DateTime NextDueUtc,
    double NextIntervalDays)
{
    public bool HintUsed { get; init; }
    public long? SessionId { get; init; }
}

public sealed record RecallModelProfileRecord(
    long Id,
    int Version,
    string WeightsJson,
    DateTime TrainedAtUtc,
    string? MetricsJson,
    bool IsActive,
    bool Accepted,
    long? TrainedThroughLogId);

public sealed record RecallPredictionRecord(
    long ProfileId,
    int ModelVersion,
    double RawRecall,
    double PredictedRecall,
    string FeaturesJson);

public sealed record CardReviewHistorySample(Grade Rating, double EffectiveElapsedMs, bool TimingValid);

public sealed record RecallTrainingObservation(
    long ReviewLogId,
    DateTime ReviewedAtUtc,
    Grade Rating,
    double RawRecall,
    string FeaturesJson);

public sealed record RecallTrainingSnapshot(
    long LatestReviewLogId,
    long LastTrainedThroughLogId,
    int LatestModelVersion,
    IReadOnlyList<RecallTrainingObservation> Observations);

public sealed record SessionStartResult(long SessionId, IReadOnlyList<long> QueueEntryIds);

public sealed record SessionQueueCardRecord(long QueueEntryId, CardRecord Card, bool IsEarlyReview = false);

public sealed record InterruptedSessionRecord(
    long SessionId,
    Intensity Intensity,
    DateTime StartedAtUtc,
    int PlannedCards,
    int NewCount,
    double EstimatedMinutes,
    int Reviews,
    int Again,
    int Hard,
    int Good,
    int Easy,
    IReadOnlyList<SessionQueueCardRecord> Queue)
{
    public StudyMode Mode { get; init; } = StudyMode.Recall;
}

public sealed record AnswerPersistenceResult(long ReviewLogId, long? RequeuedQueueEntryId);

public sealed record DailyPlanRow(long Id, string LocalDate, Intensity Intensity, int ReviewCount, int NewCount, double EstimatedMinutes, DateTime CreatedAtUtc);

/// <summary>A card presented for study, with dictionary payload.</summary>
public sealed record StudyItem(
    long CardId,
    string Lemma,
    string? IpaUs,
    string? IpaUk,
    IReadOnlyList<WordSense> Senses,
    bool IsNew,
    bool IsRelearning,
    double? Stability,
    DateTime DueUtc)
{
    public WordDetails Details { get; init; } = WordDetails.Empty;
}

public sealed record TimingResult(double RawMs, double EffectiveMs, double UnfocusedMs, bool Valid)
{
    public static TimingResult Invalid { get; } = new(0, 0, 0, false);
}
