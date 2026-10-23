using Lexico.Core.Domain;

namespace Lexico.Core.Scheduling;

/// <summary>
/// Measures effective thinking time for one card: monotonic elapsed time minus unfocused
/// (window deactivated) gaps. Effective time is an auxiliary calibration signal
/// only — it never maps to a grade by itself.
/// </summary>
public sealed class EffectiveTiming
{
    private const double MinValidMs = 300;
    private const double MaxValidMs = 120_000;

    private readonly TimeProvider _timeProvider;
    private readonly long _startTimestamp;
    private double _unfocusedMs;
    private readonly object _lock = new();

    public EffectiveTiming(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _startTimestamp = _timeProvider.GetTimestamp();
    }

    /// <summary>Record a window-deactivated span in milliseconds.</summary>
    public void AddUnfocused(double ms)
    {
        if (ms <= 0)
            return;
        lock (_lock) { _unfocusedMs += ms; }
    }

    public TimingResult Finish()
    {
        double rawMs = _timeProvider.GetElapsedTime(_startTimestamp).TotalMilliseconds;
        double unfocusedMs;
        lock (_lock) { unfocusedMs = _unfocusedMs; }
        double effectiveMs = Math.Max(0, rawMs - unfocusedMs);
        bool valid = effectiveMs is >= MinValidMs and <= MaxValidMs
                     && rawMs > 0
                     && unfocusedMs < 0.6 * rawMs;
        return new TimingResult(Math.Round(rawMs, 1), Math.Round(effectiveMs, 1), Math.Round(unfocusedMs, 1), valid);
    }
}
