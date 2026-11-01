using System.Diagnostics;
using MemoryGuardian.Core;

namespace MemoryGuardian.App;

public sealed record CleanupObservation(DateTimeOffset Time, string Reason, bool Success, uint? NtStatus,
    int? Win32Error, string Message, double DurationSeconds, MemorySnapshot? Before,
    MemorySnapshot? After, double? AvailableChangeMiB);

public sealed class MonitorHost : IDisposable
{
    private readonly AppStorage storage;
    private readonly AppSettings settings;
    private readonly PressurePolicy policy;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly SemaphoreSlim cleaning = new(1, 1);
    private readonly bool readOnly;
    private bool stopped;
    private bool busy;
    private string? error;
    private string decision = "warming-up";
    private MemorySnapshot? sample;
    private CleanupObservation? lastResult;
    public event Action? Changed;
    public bool Paused => settings.Paused;
    public bool Busy => busy;
    public bool ReadOnly => readOnly;
    public MemorySnapshot? Sample => sample;
    public string? LastError => error;
    public DateTimeOffset? LastAttempt => settings.LastAttemptUtc;
    public CleanupObservation? LastResult => lastResult;
    public bool StartupEnabled { get; private set; }

    public MonitorHost(bool readOnly)
    {
        this.readOnly = readOnly;
        storage = new(readOnly);
        settings = storage.Load();
        policy = new(new PolicyOptions(), DateTimeOffset.UtcNow, settings.LastAttemptUtc, settings.PressureCycleConsumed);
        StartupEnabled = StartupRegistration.IsEnabled();
        storage.Log("started", new { ProcessId = Environment.ProcessId, ReadOnly = readOnly, Paused, StartupEnabled });
    }

    public object Status() => new
    {
        Version = "1.0.1", Running = !stopped, ProcessId = Environment.ProcessId,
        Time = DateTimeOffset.UtcNow, UptimeSeconds = clock.Elapsed.TotalSeconds,
        Paused, Busy, ReadOnly, StartupEnabled, Decision = decision, Error = error,
        Memory = sample, LastAttemptUtc = settings.LastAttemptUtc,
        PressureCycleConsumed = policy.PressureCycleConsumed, LastResult = lastResult,
        Rules = new { TriggerStrictlyAbovePercent = 70, SamplingSeconds = 30, SustainSeconds = 0, CooldownMinutes = 30, RecoveryBelowPercent = 65 },
        DataDirectory = readOnly ? null : AppStorage.BaseDirectory
    };

    public string StatusText => error is not null ? error : readOnly ? "只读验证模式 · 不执行整理" : decision switch
    {
        "paused" => "自动整理已暂停，仍可手动整理",
        "fullscreen" => "前台应用全屏中，暂缓自动整理",
        "busy" => "正在整理工作集…",
        "high-pending" => "内存偏高，正在观察是否持续",
        "cooldown" => "整理间隔保护中，仍可手动整理",
        "cycle-consumed" => "本轮高占用已处理，等待内存恢复",
        "invalid-sample" => "本次读数无效，重新观察",
        "sample-gap" => "监测已恢复，重新观察",
        "warming-up" => "正在观察内存变化",
        _ => "自动监测中 · 暂不需要整理"
    };

    public async Task TickAsync()
    {
        if (stopped || busy) return;
        try
        {
            sample = WindowsMemory.Read();
            var coreSample = new MemorySample(sample.Time, sample.TotalBytes, sample.AvailableBytes, sample.CommitBytes);
            var result = policy.Observe(coreSample, clock.Elapsed, Paused, WindowsMemory.IsFullScreenForeground(), busy);
            decision = result.Code;
            if (settings.PressureCycleConsumed != policy.PressureCycleConsumed)
            {
                settings.PressureCycleConsumed = policy.PressureCycleConsumed;
                storage.Save(settings);
            }
            Publish();
            if (result.ShouldClean && !readOnly && error is null) await CleanAsync("automatic");
        }
        catch (Exception e) { Fail(e.Message); }
    }

    public async Task<object> CleanAsync(string reason)
    {
        if (stopped) return new { Success = false, Message = "程序已退出" };
        if (readOnly) return new { Success = false, Message = "只读验证模式不会整理内存" };
        if (!await cleaning.WaitAsync(0)) return new { Success = false, Message = "已有整理正在进行" };
        busy = true;
        decision = "busy";
        MemorySnapshot? before = null;
        var started = Stopwatch.StartNew();
        try
        {
            before = WindowsMemory.Read();
            settings.LastAttemptUtc = DateTimeOffset.UtcNow;
            policy.RecordAttempt(clock.Elapsed);
            settings.PressureCycleConsumed = policy.PressureCycleConsumed;
            storage.Save(settings); // Persist before the operation so a restart cannot immediately repeat it.
            storage.Log("cleanup-started", new { Reason = reason, Before = before });
            Publish();
            var result = await Task.Run(WindowsMemory.TrimWorkingSets);
            await Task.Delay(1500);
            sample = WindowsMemory.Read();
            lastResult = new(DateTimeOffset.UtcNow, reason, result.Success, result.NtStatus, result.Win32Error,
                result.Message, started.Elapsed.TotalSeconds, before, sample,
                ((double)sample.AvailableBytes - before.AvailableBytes) / 1024 / 1024);
            storage.Log("cleanup-finished", lastResult);
            if (!result.Success) Fail(result.Message);
            else { error = null; decision = Paused ? "paused" : "cooldown"; }
            return lastResult;
        }
        catch (Exception e)
        {
            lastResult = new(DateTimeOffset.UtcNow, reason, false, null, null, e.Message,
                started.Elapsed.TotalSeconds, before, null, null);
            try { storage.Log("cleanup-finished", lastResult); } catch { }
            Fail(e.Message);
            return lastResult;
        }
        finally
        {
            busy = false;
            cleaning.Release();
            Publish();
        }
    }

    public void SetPaused(bool paused)
    {
        settings.Paused = paused;
        policy.ResetObservation(clock.Elapsed);
        decision = paused ? "paused" : "warming-up";
        if (!paused) error = null;
        storage.Save(settings);
        storage.Log(paused ? "paused" : "resumed", new { });
        Publish();
    }

    public void ResetObservation()
    {
        policy.ResetObservation(clock.Elapsed);
        decision = Paused ? "paused" : "warming-up";
        Publish();
    }

    public void SetStartup(bool enabled)
    {
        if (readOnly) throw new InvalidOperationException("只读验证模式不会修改自启");
        if (enabled) StartupRegistration.Enable(AppStorage.InstalledExe); else StartupRegistration.Disable();
        StartupEnabled = StartupRegistration.IsEnabled();
        storage.Log("startup-changed", new { Enabled = StartupEnabled });
        Publish();
    }

    private void Fail(string message)
    {
        error = message;
        settings.Paused = true;
        policy.ResetObservation(clock.Elapsed);
        decision = "paused";
        try { storage.Save(settings); storage.Log("error", new { Message = message }); } catch { }
        Publish();
    }

    private void Publish()
    {
        try { storage.WriteState(Status()); }
        catch (Exception e) { error = "无法保存状态，自动整理已暂停：" + e.Message; settings.Paused = true; }
        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (stopped) return;
        stopped = true;
        try { storage.Log("stopped", new { ProcessId = Environment.ProcessId }); } catch { }
        Publish();
    }
}
