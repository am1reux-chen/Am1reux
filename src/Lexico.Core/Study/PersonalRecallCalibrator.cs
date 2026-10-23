using Lexico.Core.Domain;

namespace Lexico.Core.Study;

/// <summary>Deterministic, shadow-only regularized logistic recall calibration.</summary>
public static class PersonalRecallCalibrator
{
    public const int Version = 1;
    public const int MinimumTrainingExamples = 200;
    public const int FeatureCount = 9;
    public const double MaximumAdjustment = 0.15;

    private const int MinimumPerClass = 10;
    private const int MetricBins = 10;
    private const double Regularization = 0.05;

    public sealed record FeatureVector(
        double FsrsProbability,
        double RelativeThinkingTime,
        double RecentAccuracy,
        double HistoricalLapses,
        double HintUsed,
        double IntervalDays,
        double IsLearning,
        double IsReview,
        double IsRelearning)
    {
        public CardState StateBefore => IsLearning > 0.5
            ? CardState.Learning
            : IsRelearning > 0.5 ? CardState.Relearning : CardState.Review;

        public double[] ToArray() =>
        [
            FsrsProbability,
            RelativeThinkingTime,
            RecentAccuracy,
            HistoricalLapses,
            HintUsed,
            IntervalDays,
            IsLearning,
            IsReview,
            IsRelearning,
        ];
    }

    public sealed record PriorHistory(
        IReadOnlyList<Grade> GradesMostRecentFirst,
        IReadOnlyList<(double EffectiveMs, bool TimingValid)> ThinkingTimesMostRecentFirst,
        int PriorLapseCount);

    public sealed record TrainingExample(long ReviewLogId, DateTime ReviewedAtUtc, bool Recalled, FeatureVector Features);

    public sealed record Metrics(int Count, double LogLoss, double BrierScore, double ExpectedCalibrationError)
    {
        public static Metrics Empty { get; } = new(0, 0, 0, 0);
    }

    public sealed record TrainingResult(
        bool Eligible,
        bool Accepted,
        double[] Weights,
        int TrainingCount,
        int ValidationCount,
        DateTime TrainingThroughUtc,
        DateTime ValidationStartUtc,
        Metrics BaselineMetrics,
        Metrics CalibratedMetrics);

    public static FeatureVector BuildFeatures(
        double fsrsProbability,
        CardState stateBefore,
        double elapsedDays,
        double effectiveMs,
        bool timingValid,
        bool hintUsed,
        PriorHistory history)
    {
        var recentGrades = history.GradesMostRecentFirst.Take(10).ToArray();
        double recentAccuracy = recentGrades.Length == 0
            ? 0.5
            : recentGrades.Count(grade => grade != Grade.Again) / (double)recentGrades.Length;

        var priorTimes = history.ThinkingTimesMostRecentFirst
            .Where(sample => sample.TimingValid && IsValidThinkingTime(sample.EffectiveMs))
            .Select(sample => sample.EffectiveMs)
            .Take(100)
            .Order()
            .ToArray();
        double relativeThinkingTime = 0;
        if (timingValid && IsValidThinkingTime(effectiveMs) && priorTimes.Length > 0)
        {
            double median = priorTimes.Length % 2 == 0
                ? (priorTimes[priorTimes.Length / 2 - 1] + priorTimes[priorTimes.Length / 2]) / 2
                : priorTimes[priorTimes.Length / 2];
            relativeThinkingTime = Math.Clamp(Math.Log(effectiveMs / median) / 2, -1, 1);
        }

        double lapses = Math.Clamp(history.PriorLapseCount, 0, 1000);
        double normalizedLapses = Math.Log(1 + lapses) / Math.Log(1001);
        double interval = SafeNonnegative(elapsedDays);
        double normalizedInterval = Math.Log(1 + Math.Min(interval, 36_500)) / Math.Log(36_501);

        return new FeatureVector(
            ClampProbability(fsrsProbability),
            relativeThinkingTime,
            recentAccuracy,
            normalizedLapses,
            hintUsed ? 1 : 0,
            normalizedInterval,
            stateBefore == CardState.Learning ? 1 : 0,
            stateBefore == CardState.Review ? 1 : 0,
            stateBefore == CardState.Relearning ? 1 : 0);
    }

    public static TrainingResult Train(IReadOnlyList<TrainingExample> examples)
    {
        ArgumentNullException.ThrowIfNull(examples);
        var chronological = examples
            .OrderBy(example => AsUtc(example.ReviewedAtUtc))
            .ThenBy(example => example.ReviewLogId)
            .ToArray();

        int split = (int)(chronological.Length * 0.8);
        var training = chronological[..split];
        var validation = chronological[split..];
        DateTime trainingThrough = training.Length == 0 ? DateTime.MinValue : AsUtc(training[^1].ReviewedAtUtc);
        DateTime validationStart = validation.Length == 0 ? DateTime.MinValue : AsUtc(validation[0].ReviewedAtUtc);
        var empty = new TrainingResult(false, false, [], training.Length, validation.Length,
            trainingThrough, validationStart, Metrics.Empty, Metrics.Empty);

        if (chronological.Length < MinimumTrainingExamples || training.Length < 100 || validation.Length < 30)
            return empty;
        if (CountLabels(training, recalled: true) < MinimumPerClass || CountLabels(training, recalled: false) < MinimumPerClass ||
            CountLabels(validation, recalled: true) < MinimumPerClass || CountLabels(validation, recalled: false) < MinimumPerClass)
            return empty;

        var weights = Fit(training);
        var baseline = Evaluate(validation.Select(example => example.Features.FsrsProbability).ToArray(),
            validation.Select(example => example.Recalled).ToArray());
        var calibratedProbabilities = validation.Select(example => Apply(weights, example.Features)).ToArray();
        var calibrated = Evaluate(calibratedProbabilities, validation.Select(example => example.Recalled).ToArray());
        bool accepted = calibrated.LogLoss < baseline.LogLoss - 1e-6 &&
                        calibrated.BrierScore < baseline.BrierScore - 1e-6 &&
                        calibrated.ExpectedCalibrationError <= baseline.ExpectedCalibrationError + 0.01;

        return new TrainingResult(true, accepted, weights, training.Length, validation.Length,
            trainingThrough, validationStart, baseline, calibrated);
    }

    public static double Predict(double fsrsProbability, FeatureVector features, IReadOnlyList<double>? weights, bool accepted)
    {
        double raw = ClampProbability(fsrsProbability);
        if (!accepted || weights is null || weights.Count != FeatureCount + 1)
            return raw;

        double calibrated = Apply(weights, features);
        return Math.Clamp(calibrated, Math.Max(0, raw - MaximumAdjustment), Math.Min(1, raw + MaximumAdjustment));
    }

    public static Metrics Evaluate(IReadOnlyList<double> probabilities, IReadOnlyList<bool> outcomes)
    {
        ArgumentNullException.ThrowIfNull(probabilities);
        ArgumentNullException.ThrowIfNull(outcomes);
        if (probabilities.Count != outcomes.Count)
            throw new ArgumentException("Prediction and outcome counts must match.");
        if (probabilities.Count == 0)
            return Metrics.Empty;

        double logLoss = 0, brier = 0;
        var counts = new int[MetricBins];
        var probabilitySums = new double[MetricBins];
        var outcomeSums = new double[MetricBins];
        for (int i = 0; i < probabilities.Count; i++)
        {
            double p = Math.Clamp(ClampProbability(probabilities[i]), 1e-7, 1 - 1e-7);
            double outcome = outcomes[i] ? 1 : 0;
            logLoss -= outcome * Math.Log(p) + (1 - outcome) * Math.Log(1 - p);
            brier += (p - outcome) * (p - outcome);

            int bin = Math.Min(MetricBins - 1, (int)(p * MetricBins));
            counts[bin]++;
            probabilitySums[bin] += p;
            outcomeSums[bin] += outcome;
        }

        double ece = 0;
        for (int bin = 0; bin < MetricBins; bin++)
        {
            if (counts[bin] == 0)
                continue;
            ece += counts[bin] / (double)probabilities.Count *
                   Math.Abs(probabilitySums[bin] / counts[bin] - outcomeSums[bin] / counts[bin]);
        }

        return new Metrics(probabilities.Count, logLoss / probabilities.Count, brier / probabilities.Count, ece);
    }

    private static double[] Fit(IReadOnlyList<TrainingExample> examples)
    {
        var rows = examples.Select(example => (Features: example.Features.ToArray(), Recalled: example.Recalled)).ToArray();
        var weights = new double[FeatureCount + 1];
        double positives = CountLabels(examples, recalled: true);
        weights[0] = Math.Log(positives / (examples.Count - positives));

        const double learningRate = 0.1;
        for (int iteration = 0; iteration < 4000; iteration++)
        {
            var gradient = new double[weights.Length];
            foreach (var row in rows)
            {
                var features = row.Features;
                double error = Apply(weights, features) - (row.Recalled ? 1 : 0);
                gradient[0] += error;
                for (int i = 0; i < features.Length; i++)
                    gradient[i + 1] += error * features[i];
            }

            double maximumGradient = 0;
            gradient[0] /= examples.Count;
            maximumGradient = Math.Abs(gradient[0]);
            for (int i = 1; i < gradient.Length; i++)
            {
                gradient[i] = gradient[i] / examples.Count + Regularization * weights[i];
                maximumGradient = Math.Max(maximumGradient, Math.Abs(gradient[i]));
            }

            for (int i = 0; i < weights.Length; i++)
                weights[i] -= learningRate * gradient[i];
            if (maximumGradient < 1e-8)
                break;
        }

        return weights;
    }

    private static double Apply(IReadOnlyList<double> weights, FeatureVector features)
        => Apply(weights, features.ToArray());

    private static double Apply(IReadOnlyList<double> weights, IReadOnlyList<double> features)
    {
        double score = weights[0];
        for (int i = 0; i < features.Count; i++)
            score += weights[i + 1] * features[i];
        return Sigmoid(score);
    }

    private static int CountLabels(IEnumerable<TrainingExample> examples, bool recalled)
        => examples.Count(example => example.Recalled == recalled);

    private static double Sigmoid(double value)
    {
        double bounded = Math.Clamp(value, -30, 30);
        return 1 / (1 + Math.Exp(-bounded));
    }

    private static double ClampProbability(double value)
        => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    private static bool IsValidThinkingTime(double value)
        => double.IsFinite(value) && value is >= 300 and <= 120_000;

    private static double SafeNonnegative(double value)
        => double.IsFinite(value) ? Math.Max(0, value) : 0;

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
