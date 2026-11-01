namespace MemoryGuardian.Core;

public sealed record MemorySample(DateTimeOffset Time, ulong TotalBytes, ulong AvailableBytes, ulong CommitBytes)
{
    public double UsedPercent => TotalBytes == 0 ? 0 : 100.0 * (TotalBytes - Math.Min(TotalBytes, AvailableBytes)) / TotalBytes;
}

public sealed record PolicyOptions
{
    public double TriggerPercent { get; init; } = 70;
    public TimeSpan HighDuration { get; init; } = TimeSpan.Zero;
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromMinutes(30);
    public double RecoveryPercent { get; init; } = 65;
    public TimeSpan RecoveryDuration { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan MaximumSampleGap { get; init; } = TimeSpan.FromSeconds(75);
}

public sealed record PolicyDecision(bool ShouldClean, string Code, TimeSpan? Remaining = null);

public sealed class PressurePolicy
{
    private readonly PolicyOptions _options;
    private readonly TimeSpan _initialCooldownRemaining;
    private TimeSpan? _attemptUptime;
    private TimeSpan? _lastUptime;
    private TimeSpan? _observationSince;
    private TimeSpan? _highSince;
    private TimeSpan? _recoverySince;

    public PressurePolicy(PolicyOptions options, DateTimeOffset startedUtc, DateTimeOffset? lastAttemptUtc = null, bool pressureCycleConsumed = false)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        PressureCycleConsumed = pressureCycleConsumed;
        if (lastAttemptUtc is { } last)
        {
            var elapsed = startedUtc > last ? startedUtc - last : TimeSpan.Zero;
            _initialCooldownRemaining = elapsed < options.Cooldown ? options.Cooldown - elapsed : TimeSpan.Zero;
        }
    }

    public bool PressureCycleConsumed { get; private set; }

    public PolicyDecision Observe(MemorySample sample, TimeSpan uptime, bool paused, bool fullScreen, bool busy = false)
    {
        if (uptime < TimeSpan.Zero || (_lastUptime is { } previous && uptime < previous))
        {
            ResetObservation(uptime);
            return new(false, "invalid-sample");
        }

        var gap = _lastUptime is { } last && uptime - last > _options.MaximumSampleGap;
        _lastUptime = uptime;
        string? blocked = paused ? "paused" : fullScreen ? "fullscreen" : busy ? "busy"
            : sample.TotalBytes == 0 || sample.AvailableBytes > sample.TotalBytes ? "invalid-sample"
            : gap ? "sample-gap" : null;
        if (blocked is not null)
        {
            ResetObservation(uptime);
            return new(false, blocked);
        }

        _observationSince ??= uptime;
        if (sample.UsedPercent < _options.RecoveryPercent)
        {
            _recoverySince ??= uptime;
            if (uptime - _recoverySince.Value >= _options.RecoveryDuration)
                PressureCycleConsumed = false;
        }
        else _recoverySince = null;

        var high = sample.UsedPercent > _options.TriggerPercent;
        if (high) _highSince ??= uptime;
        else _highSince = null;

        // Only UTC at construction restores persistence; every later interval uses uptime.
        var elapsedCooldown = _attemptUptime is { } attempt ? MaxZero(uptime - attempt) : uptime;
        var cooldown = (_attemptUptime.HasValue ? _options.Cooldown : _initialCooldownRemaining) - elapsedCooldown;
        if (cooldown > TimeSpan.Zero) return new(false, "cooldown", cooldown);
        if (high && PressureCycleConsumed) return new(false, "cycle-consumed");

        var startupRemaining = _options.HighDuration - (uptime - _observationSince.Value);
        if (startupRemaining > TimeSpan.Zero) return new(false, "warming-up", startupRemaining);
        if (!high) return new(false, "normal");

        var highRemaining = _options.HighDuration - (uptime - _highSince!.Value);
        return highRemaining > TimeSpan.Zero
            ? new(false, "high-pending", highRemaining)
            : new(true, "ready");
    }

    public void ResetObservation(TimeSpan uptime)
    {
        _lastUptime = uptime;
        _observationSince = null;
        _highSince = null;
        _recoverySince = null;
    }

    public void RecordAttempt(TimeSpan uptime)
    {
        _attemptUptime = uptime;
        PressureCycleConsumed = true;
        ResetObservation(uptime);
    }

    private static TimeSpan MaxZero(TimeSpan value) => value > TimeSpan.Zero ? value : TimeSpan.Zero;
}
