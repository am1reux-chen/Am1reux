namespace Lexico.Core.Domain;

public sealed record WordSense(long Id, string? Pos, string? ZhGloss, string? EnGloss, string? ExampleEn, string? ExampleZh);

public sealed record WordEntry(
    long Id,
    string Lemma,
    string? IpaUs,
    string? IpaUk,
    int? FrequencyRank,
    string? Cefr,
    IReadOnlyList<WordSense> Senses)
{
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
    double NextIntervalDays);

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
    DateTime DueUtc);

public sealed record TimingResult(double RawMs, double EffectiveMs, double UnfocusedMs, bool Valid)
{
    public static TimingResult Invalid { get; } = new(0, 0, 0, false);
}
