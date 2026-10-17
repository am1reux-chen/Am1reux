namespace Lexico.Core.Domain;

/// <summary>FSRS card lifecycle state. Mirrors FSRS semantics; "new" is Learning with Reps == 0.</summary>
public enum CardState
{
    Learning = 0,
    Review = 1,
    Relearning = 2,
}

/// <summary>Review grade (FSRS rating).</summary>
public enum Grade
{
    Again = 1,
    Hard = 2,
    Good = 3,
    Easy = 4,
}

/// <summary>Daily intensity. Ratios are initial policy and adjustable in DailyPlanner.</summary>
public enum Intensity
{
    Light = 1,
    Normal = 2,
    Heavy = 3,
}
