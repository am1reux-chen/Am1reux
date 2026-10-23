using Lexico.Core.Domain;
using Lexico.Core.Study;
using Xunit;

namespace Lexico.Core.Tests;

public sealed class PersonalRecallCalibratorTests
{
    [Fact]
    public void InsufficientDataFallsBackToFsrsProbability()
    {
        var examples = CreateExamples(count: 199, fsrsProbability: .9, positiveEvery: 7);

        var training = PersonalRecallCalibrator.Train(examples);
        var prediction = PersonalRecallCalibrator.Predict(.9, Features(.9), training.Weights, training.Accepted);

        Assert.False(training.Eligible);
        Assert.False(training.Accepted);
        Assert.Empty(training.Weights);
        Assert.Equal(.9, prediction);
    }

    [Fact]
    public void TrainingUsesChronologicalHoldoutAndImprovesMiscalibratedBaseline()
    {
        var examples = CreateExamples(count: 300, fsrsProbability: .9, positiveEvery: 7)
            .OrderByDescending(example => example.ReviewedAtUtc)
            .ToArray();

        var training = PersonalRecallCalibrator.Train(examples);

        Assert.True(training.Eligible);
        Assert.Equal(240, training.TrainingCount);
        Assert.Equal(60, training.ValidationCount);
        Assert.True(training.TrainingThroughUtc < training.ValidationStartUtc);
        Assert.True(training.Accepted);
        Assert.True(training.CalibratedMetrics.LogLoss < training.BaselineMetrics.LogLoss);
        Assert.True(training.CalibratedMetrics.BrierScore < training.BaselineMetrics.BrierScore);

        var changedHoldout = examples.Select(example => example.ReviewedAtUtc >= training.ValidationStartUtc
            ? example with { Recalled = !example.Recalled }
            : example).ToArray();
        var changedTraining = PersonalRecallCalibrator.Train(changedHoldout);
        Assert.Equal(training.Weights, changedTraining.Weights);

        var prediction = PersonalRecallCalibrator.Predict(.9, Features(.9), training.Weights, training.Accepted);
        Assert.InRange(prediction, .75, .9);
    }

    [Fact]
    public void ReactionTimeIsValidOnlyWhenFocusedAndClampedAsAnAuxiliaryFeature()
    {
        var history = new PersonalRecallCalibrator.PriorHistory(
            GradesMostRecentFirst: [Grade.Good, Grade.Again, Grade.Hard],
            ThinkingTimesMostRecentFirst: [(10_000, true), (5_000, false), (20_000, true)],
            PriorLapseCount: 4);

        var valid = PersonalRecallCalibrator.BuildFeatures(
            fsrsProbability: .65, stateBefore: CardState.Review, elapsedDays: 14,
            effectiveMs: 30_000, timingValid: true, hintUsed: true, history);
        var invalid = PersonalRecallCalibrator.BuildFeatures(
            fsrsProbability: .65, stateBefore: CardState.Review, elapsedDays: 14,
            effectiveMs: 300_000, timingValid: false, hintUsed: false, history);

        Assert.Equal(Math.Log(2) / 2, valid.RelativeThinkingTime, 12);
        Assert.Equal(0, invalid.RelativeThinkingTime);
        Assert.Equal(2d / 3, valid.RecentAccuracy, 10);
        Assert.Equal(1, valid.HintUsed);
        Assert.InRange(valid.HistoricalLapses, 0, 1);
        Assert.Equal(CardState.Review, valid.StateBefore);
        Assert.Equal(0, invalid.HintUsed);
    }

    [Fact]
    public void MetricsReportLogLossBrierAndExpectedCalibrationError()
    {
        var metrics = PersonalRecallCalibrator.Evaluate([.8, .2], [true, false]);

        Assert.Equal(-Math.Log(.8), metrics.LogLoss, 12);
        Assert.Equal(.04, metrics.BrierScore, 12);
        Assert.Equal(.2, metrics.ExpectedCalibrationError, 12);
    }

    private static PersonalRecallCalibrator.FeatureVector Features(double raw) => new(
        raw, 0, .5, 0, 0, 0, 0, 1, 0);

    private static List<PersonalRecallCalibrator.TrainingExample> CreateExamples(int count, double fsrsProbability, int positiveEvery)
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return Enumerable.Range(0, count)
            .Select(index => new PersonalRecallCalibrator.TrainingExample(
                ReviewLogId: index + 1,
                ReviewedAtUtc: start.AddHours(index),
                Recalled: index % 10 < positiveEvery,
                Features: Features(fsrsProbability)))
            .ToList();
    }
}
