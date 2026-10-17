using Lexico.Core.Domain;
using Lexico.Core.Scheduling;
using Xunit;

namespace Lexico.Core.Tests;

public class FsrsEngineTests
{
    private static SrsCardSnapshot NewCard(DateTime now) =>
        new(CardState.Learning, 0, null, null, now, null);

    [Fact]
    public void Again_OnNewCard_KeepsLearningWithShortInterval()
    {
        var engine = new FsrsCoreEngine(enableFuzzing: false);
        var now = DateTime.UtcNow;
        var outcome = engine.Schedule(NewCard(now), Grade.Again, now);

        Assert.Equal(CardState.Learning, outcome.UpdatedCard.State);
        Assert.Equal(0, outcome.UpdatedCard.Step);
        Assert.True(outcome.NextIntervalDays <= 5.0 / 1440.0, $"interval was {outcome.NextIntervalDays} days");
        Assert.NotNull(outcome.UpdatedCard.Stability); // FSRS initializes S = w[0] on the first review
        Assert.True(outcome.UpdatedCard.Stability!.Value > 0);
    }

    [Fact]
    public void Good_OnNewCard_AdvancesOrGraduates()
    {
        var engine = new FsrsCoreEngine(enableFuzzing: false);
        var now = DateTime.UtcNow;
        var outcome = engine.Schedule(NewCard(now), Grade.Good, now);

        Assert.True(outcome.UpdatedCard.State is CardState.Learning or CardState.Review);
        if (outcome.UpdatedCard.State == CardState.Review)
        {
            Assert.NotNull(outcome.UpdatedCard.Stability);
            Assert.True(outcome.NextIntervalDays >= 0.5, "graduated interval should be at least half a day");
        }
    }

    [Fact]
    public void Review_IntervalsGrow_WithConsecutiveGood()
    {
        var engine = new FsrsCoreEngine(enableFuzzing: false);
        var now = DateTime.UtcNow;
        var card = NewCard(now);

        // Graduate the card (learning steps), whatever that takes.
        CardState lastState;
        int guard = 0;
        do
        {
            var outcome = engine.Schedule(card, Grade.Good, now);
            card = outcome.UpdatedCard;
            lastState = card.State;
        } while (lastState == CardState.Learning && ++guard < 10);

        Assert.Equal(CardState.Review, card.State);

        double prevInterval = 0;
        for (int i = 0; i < 3; i++)
        {
            now = now.AddDays(Math.Max(1, prevInterval));
            var outcome = engine.Schedule(card, Grade.Good, now);
            card = outcome.UpdatedCard;
            Assert.True(outcome.NextIntervalDays > prevInterval,
                $"interval should grow: {outcome.NextIntervalDays} <= {prevInterval}");
            prevInterval = outcome.NextIntervalDays;
        }
    }

    [Fact]
    public void PredictRecall_IsZeroForNew_AndDecaysOverTime()
    {
        var engine = new FsrsCoreEngine(enableFuzzing: false);
        var now = DateTime.UtcNow;
        var outcome = engine.Schedule(NewCard(now), Grade.Good, now);
        var card = outcome.UpdatedCard;

        if (card.LastReviewUtc is not null && card.Stability is not null)
        {
            double soon = engine.PredictRecall(card, now.AddMinutes(1));
            double later = engine.PredictRecall(card, now.AddDays(30));
            Assert.InRange(soon, 0, 1);
            Assert.InRange(later, 0, 1);
            Assert.True(later < soon, "recall probability must decay with elapsed time");
        }
        else
        {
            Assert.Equal(0, engine.PredictRecall(NewCard(now), now));
        }
    }

    [Fact]
    public void Schedule_IsDeterministic_WithFuzzingDisabled()
    {
        var engine = new FsrsCoreEngine(enableFuzzing: false);
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var a = engine.Schedule(NewCard(now), Grade.Good, now);
        var b = engine.Schedule(NewCard(now), Grade.Good, now);

        Assert.Equal(a.UpdatedCard.DueUtc, b.UpdatedCard.DueUtc);
        Assert.Equal(a.UpdatedCard.Stability, b.UpdatedCard.Stability);
        Assert.Equal(a.NextIntervalDays, b.NextIntervalDays);
    }

    [Fact]
    public void Forgot_ReviewCard_EntersRelearning()
    {
        var engine = new FsrsCoreEngine(enableFuzzing: false);
        var now = DateTime.UtcNow;
        var card = NewCard(now);
        int guard = 0;
        do
        {
            card = engine.Schedule(card, Grade.Good, now).UpdatedCard;
        } while (card.State == CardState.Learning && ++guard < 10);

        now = now.AddDays(10);
        var forgotten = engine.Schedule(card, Grade.Again, now);

        Assert.Equal(CardState.Relearning, forgotten.UpdatedCard.State);
        Assert.True(forgotten.WasRelearned == false); // it *entered* relearning, did not leave it
        Assert.True(forgotten.NextIntervalDays <= 30.0 / 1440.0, "relearning step should be short");
    }
}
