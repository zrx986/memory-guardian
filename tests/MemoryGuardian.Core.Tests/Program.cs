using MemoryGuardian.Core;

var tests = new List<(string Name, Action Run)>();
var epoch = DateTimeOffset.Parse("2026-11-01T00:00:00Z");
const ulong GiB = 1024UL * 1024 * 1024;
MemorySample High() => new(epoch, 10 * GiB, 2 * GiB, 0);
MemorySample Low() => new(epoch, 10 * GiB, 4 * GiB, 0);
PressurePolicy New(DateTimeOffset? last = null, bool consumed = false) => new(new(), epoch, last, consumed);
PolicyDecision At(PressurePolicy p, double seconds, MemorySample? sample = null, bool paused = false, bool full = false, bool busy = false) =>
    p.Observe(sample ?? High(), TimeSpan.FromSeconds(seconds), paused, full, busy);
PolicyDecision Drive(PressurePolicy p, int start, int end, MemorySample? sample = null)
{
    PolicyDecision result = new(false, "normal");
    for (var t = start; t <= end; t += 30) result = At(p, t, sample);
    return result;
}
void Equal<T>(T expected, T actual, string reason = "")
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{reason} expected={expected}, actual={actual}");
}
void Test(string name, Action run) => tests.Add((name, run));

Test("defaults follow latest user threshold", () => {
    var options = new PolicyOptions();
    Equal(70d, options.TriggerPercent); Equal(TimeSpan.Zero, options.HighDuration);
    Equal(65d, options.RecoveryPercent); Equal(TimeSpan.FromSeconds(120), options.RecoveryDuration);
    Equal(TimeSpan.FromMinutes(30), options.Cooldown);
});
Test("available limit option has been removed", () => Equal(true, typeof(PolicyOptions).GetProperty("AvailableLimitBytes") is null));
Test("UsedPercent handles zero and excessive available", () => {
    Equal(0d, new MemorySample(epoch, 0, GiB, 0).UsedPercent);
    Equal(0d, new MemorySample(epoch, GiB, 2 * GiB, 0).UsedPercent);
    Equal(70d, new MemorySample(epoch, 10 * GiB, 3 * GiB, 0).UsedPercent);
});
Test("first valid high sample is immediately ready", () => {
    var result = At(New(), 0); Equal(true, result.ShouldClean); Equal("ready", result.Code); Equal(true, result.Remaining is null);
});
Test("exactly 70 percent never triggers", () => Equal(false, Drive(New(), 0, 240, new(epoch, 10 * GiB, 3 * GiB, 0)).ShouldClean));
Test("just below 70 percent never triggers", () => Equal(false, Drive(New(), 0, 240, new(epoch, 10000, 3001, 0)).ShouldClean));
Test("just above 70 percent triggers immediately", () => Equal(true, At(New(), 0, new(epoch, 10000, 2999, 0)).ShouldClean));
Test("available above 3 GiB does not block high usage", () => Equal(true, At(New(), 0, new(epoch, 16 * GiB, 4 * GiB, 0)).ShouldClean));
Test("low available alone is insufficient", () => Equal(false, Drive(New(), 0, 240, new(epoch, 4 * GiB, 2 * GiB, 0)).ShouldClean));
Test("normal sample does not require startup warmup", () => Equal("normal", At(New(), 0, Low()).Code));
Test("first pressure after normal sample is immediately ready", () => {
    var p = New(); At(p, 0, Low()); Equal(true, At(p, 30).ShouldClean);
});
Test("brief pressure spike can trigger without duration gate", () => {
    var p = New(); At(p, 0, Low()); Equal(true, At(p, 30).ShouldClean);
    p.RecordAttempt(TimeSpan.FromSeconds(30)); Equal(false, At(p, 60, Low()).ShouldClean);
});
Test("explicit custom duration still supports warming-up", () => {
    var p = new PressurePolicy(new() { HighDuration = TimeSpan.FromSeconds(120) }, epoch);
    Equal("warming-up", At(p, 0).Code); Equal(false, Drive(p, 30, 90).ShouldClean);
    Equal(true, At(p, 120).ShouldClean);
});
Test("explicit custom duration still supports high-pending", () => {
    var p = new PressurePolicy(new() { HighDuration = TimeSpan.FromSeconds(120) }, epoch);
    Drive(p, 0, 60, Low()); At(p, 90); Equal("high-pending", At(p, 120).Code);
    Drive(p, 150, 180); Equal(true, At(p, 210).ShouldClean);
});
Test("attempt records cycle and full cooldown", () => {
    var p = New(); p.RecordAttempt(TimeSpan.Zero); Equal(true, p.PressureCycleConsumed);
    var decision = At(p, 30); Equal("cooldown", decision.Code); Equal(TimeSpan.FromSeconds(1770), decision.Remaining!.Value);
});
Test("continuous pressure never repeats after cooldown", () => {
    var p = New(); p.RecordAttempt(TimeSpan.Zero); Equal("cycle-consumed", Drive(p, 0, 7200).Code);
});
Test("recovery duration unlocks consumed cycle", () => {
    var p = New(consumed: true); Drive(p, 0, 90, Low()); Equal(true, p.PressureCycleConsumed);
    At(p, 120, Low()); Equal(false, p.PressureCycleConsumed); Equal(true, At(p, 150).ShouldClean);
});
Test("exactly 65 percent does not recover", () => {
    var p = New(consumed: true); Drive(p, 0, 240, new(epoch, 20 * GiB, 7 * GiB, 0)); Equal(true, p.PressureCycleConsumed);
});
Test("just below 65 percent recovers at 120 seconds", () => {
    var p = New(consumed: true); var below = new MemorySample(epoch, 10000, 3501, 0);
    Drive(p, 0, 90, below); Equal(true, p.PressureCycleConsumed);
    At(p, 120, below); Equal(false, p.PressureCycleConsumed);
});
Test("interrupted recovery restarts duration", () => {
    var p = New(consumed: true); Drive(p, 0, 90, Low()); At(p, 120);
    Drive(p, 150, 240, Low()); Equal(true, p.PressureCycleConsumed);
    At(p, 270, Low()); Equal(false, p.PressureCycleConsumed);
});
Test("manual attempt enforces automatic cooldown after recovery", () => {
    var p = New(); p.RecordAttempt(TimeSpan.Zero); Drive(p, 0, 120, Low());
    Equal(false, p.PressureCycleConsumed); Equal("cooldown", Drive(p, 150, 1770).Code);
    Equal(true, At(p, 1800).ShouldClean);
});
Test("manual attempt can be recorded during cooldown and resets it", () => {
    var p = New(); p.RecordAttempt(TimeSpan.Zero); At(p, 30);
    p.RecordAttempt(TimeSpan.FromSeconds(60));
    Equal(TimeSpan.FromMinutes(30), At(p, 60, Low()).Remaining!.Value);
    Drive(p, 90, 180, Low()); Equal(false, p.PressureCycleConsumed);
    Equal(false, Drive(p, 210, 1830).ShouldClean); Equal(true, At(p, 1860).ShouldClean);
});
Test("restart restores remaining cooldown", () => {
    var p = New(epoch.AddMinutes(-10)); Equal(TimeSpan.FromMinutes(20), At(p, 0).Remaining!.Value);
    Equal(false, Drive(p, 30, 1170).ShouldClean); Equal(true, At(p, 1200).ShouldClean);
});
Test("restart preserves consumed cycle", () => Equal("cycle-consumed", At(New(epoch.AddHours(-1), true), 0).Code));
Test("future last attempt is at most one full cooldown", () => {
    var p = New(epoch.AddDays(100)); Equal(TimeSpan.FromMinutes(30), At(p, 0).Remaining!.Value);
    Drive(p, 30, 1770); Equal(true, At(p, 1800).ShouldClean);
});
Test("old last attempt has no remaining cooldown", () => Equal(true, At(New(epoch.AddHours(-2)), 0).ShouldClean));
foreach (var blocker in new[] { "paused", "fullscreen", "busy" })
{
    Test(blocker + " blocks current sample then next valid sample is ready", () => {
        var p = New();
        var blocked = At(p, 0, paused: blocker == "paused", full: blocker == "fullscreen", busy: blocker == "busy");
        Equal(false, blocked.ShouldClean); Equal(blocker, blocked.Code); Equal(true, At(p, 30).ShouldClean);
    });
    Test(blocker + " interrupts recovery", () => {
        var p = New(consumed: true); Drive(p, 0, 90, Low());
        At(p, 120, Low(), paused: blocker == "paused", full: blocker == "fullscreen", busy: blocker == "busy");
        Drive(p, 150, 240, Low()); Equal(true, p.PressureCycleConsumed);
        At(p, 270, Low()); Equal(false, p.PressureCycleConsumed);
    });
}
Test("wake reset allows first valid high sample without extra delay", () => {
    var p = New(); At(p, 0, Low()); p.ResetObservation(TimeSpan.FromSeconds(1000)); Equal(true, At(p, 1000).ShouldClean);
});
Test("wake reset preserves consumed cycle", () => {
    var p = New(consumed: true); Drive(p, 0, 90, Low()); p.ResetObservation(TimeSpan.FromSeconds(1000));
    Drive(p, 1000, 1090, Low()); Equal(true, p.PressureCycleConsumed); At(p, 1120, Low()); Equal(false, p.PressureCycleConsumed);
});
Test("sample gap above 75 seconds blocks current sample", () => {
    var p = New(); At(p, 0, Low()); Equal("sample-gap", At(p, 76).Code); Equal(true, At(p, 106).ShouldClean);
});
Test("sample gap interrupts recovery", () => {
    var p = New(consumed: true); Drive(p, 0, 90, Low()); Equal("sample-gap", At(p, 166, Low()).Code);
    Drive(p, 196, 286, Low()); Equal(true, p.PressureCycleConsumed); At(p, 316, Low()); Equal(false, p.PressureCycleConsumed);
});
Test("sample gap equal to 75 seconds is allowed", () => {
    var p = New(); At(p, 0, Low()); Equal(true, At(p, 75).ShouldClean);
});
Test("backwards uptime blocks current sample", () => {
    var p = New(); Drive(p, 0, 90, Low()); Equal("invalid-sample", At(p, 60).Code); Equal(true, At(p, 90).ShouldClean);
});
Test("negative uptime is invalid", () => Equal("invalid-sample", At(New(), -1).Code));
foreach (var invalid in new[] { new MemorySample(epoch, 0, 0, 0), new MemorySample(epoch, GiB, 2 * GiB, 0) })
    Test("invalid memory blocks current sample " + invalid.TotalBytes, () => {
        var p = New(); Equal("invalid-sample", At(p, 0, invalid).Code); Equal(true, At(p, 30).ShouldClean);
    });
Test("wall clock jumps do not alter cooldown", () => {
    var p = New(); p.RecordAttempt(TimeSpan.Zero);
    Equal(TimeSpan.FromSeconds(1770), At(p, 30, High() with { Time = epoch.AddYears(10) }).Remaining!.Value);
    Equal(TimeSpan.FromSeconds(1740), At(p, 60, High() with { Time = epoch.AddYears(-10) }).Remaining!.Value);
});
Test("24 hour alternating pressure simulation", () => {
    var p = New(); var attempts = new List<int>();
    for (var t = 0; t < 24 * 60 * 60; t += 30)
    {
        var result = At(p, t, t % 600 < 360 ? High() : Low());
        if (!result.ShouldClean) continue;
        attempts.Add(t); p.RecordAttempt(TimeSpan.FromSeconds(t));
    }
    Equal(48, attempts.Count); Equal(0, attempts[0]);
    for (var i = 1; i < attempts.Count; i++) Equal(true, attempts[i] - attempts[i - 1] >= 1800);
});
Test("24 hour sustained pressure attempts only once", () => {
    var p = New(); var count = 0;
    for (var t = 0; t < 24 * 60 * 60; t += 30)
        if (At(p, t).ShouldClean) { count++; p.RecordAttempt(TimeSpan.FromSeconds(t)); }
    Equal(1, count);
});

var failures = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception ex) { failures++; Console.WriteLine($"FAIL {test.Name}: {ex.Message}"); }
}
Console.WriteLine($"RESULT {tests.Count - failures}/{tests.Count} passed; {failures} failed.");
return failures == 0 ? 0 : 1;
