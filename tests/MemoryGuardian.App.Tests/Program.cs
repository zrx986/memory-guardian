using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using MemoryGuardian.App;

internal static class Program
{
    private static int passed, failed;
    [STAThread]
    private static int Main()
    {
        TestControlAsync().GetAwaiter().GetResult();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        TestDisplay();
        Console.WriteLine($"RESULT: {passed} passed, {failed} failed; simulated cleanup only; no startup task changes");
        return failed == 0 ? 0 : 1;
    }

    private static async Task TestControlAsync()
    {
        var pipeName = "MemoryGuardian_Test_" + Guid.NewGuid().ToString("N");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool busy = false, paused = false, stopQueued = false;
        int dispatchCount = 0;
        var server = new LocalControl(async command =>
        {
            Interlocked.Increment(ref dispatchCount);
            if (command == "clean")
            {
                busy = true;
                started.TrySetResult();
                await release.Task;
                busy = false;
                return new { Success = true, Message = "simulated cleanup completed", AvailableChangeMiB = 512d };
            }
            if (command == "pause") paused = true;
            if (command == "stop") stopQueued = true;
            return new { Running = true, Busy = busy, Paused = paused, StopQueued = stopQueued };
        }, pipeName);
        var elevated = WindowsMemory.IsAdministrator();
        Console.WriteLine("CONTEXT: Elevated=" + elevated);
        var pipeInstances = (NamedPipeServerStream[])typeof(LocalControl).GetField("pipes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        Check(pipeInstances.Length == 4, "pipe concurrency is bounded to four instances");
        foreach (var pipe in pipeInstances)
        {
            var rules = pipe.GetAccessControl().GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToArray();
            var network = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);
            var user = new SecurityIdentifier(AppStorage.UserSid);
            Check(rules.Any(rule => rule.IdentityReference.Equals(network) && rule.AccessControlType == AccessControlType.Deny && rule.PipeAccessRights.HasFlag(PipeAccessRights.FullControl)) &&
                !rules.Any(rule => rule.AccessControlType == AccessControlType.Allow && !rule.IdentityReference.Equals(user) &&
                    !rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)) &&
                    !rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null))), "instance ACL rejects Network and allows only current user/admins/SYSTEM");
            Check(rules.Where(rule => rule.IdentityReference.Equals(user) && rule.AccessControlType == AccessControlType.Allow)
                .Any(rule => rule.PipeAccessRights.HasFlag(PipeAccessRights.CreateNewInstance)) == !elevated,
                "current-user instance creation right is present only for unelevated host");
        }
        try
        {
            using var duplicate = new LocalControl(_ => Task.FromResult<object>(new { }), pipeName);
            Check(false, "FirstPipeInstance prevents a second server claiming endpoint");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Check(true, "FirstPipeInstance prevents a second server claiming endpoint"); }
        var serving = server.RunAsync();
        Task<string> Call(string command) => LocalControl.CallAsync(command, pipeName: pipeName);
        var clean = Call("clean");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var watch = Stopwatch.StartNew();
        try
        {
            await Task.WhenAll(new[] { "status", "pause", "stop" }.Select(async command =>
            {
                try
                {
                    using var response = JsonDocument.Parse(await Call(command));
                    Check(response.RootElement.GetProperty("Running").GetBoolean() &&
                        response.RootElement.GetProperty("Busy").GetBoolean(), $"{command} responds Running=true, Busy=true during blocked cleanup");
                    if (command == "pause") Check(response.RootElement.GetProperty("Paused").GetBoolean(), "pause is applied before cleanup finishes");
                    if (command == "stop") Check(response.RootElement.GetProperty("StopQueued").GetBoolean(), "stop is queued before cleanup finishes");
                }
                catch (TimeoutException) { Check(false, $"{command} connection timed out during blocked cleanup"); }
            }));
            Check(watch.Elapsed < TimeSpan.FromSeconds(1.2), "parallel control requests fit unchanged 1.2s connect deadline");
            Check(!clean.IsCompleted, "clean waits for final result rather than returning acceptance");
        }
        finally { release.TrySetResult(); }
        using var final = JsonDocument.Parse(await clean);
        Check(final.RootElement.GetProperty("Success").GetBoolean() && final.RootElement.GetProperty("AvailableChangeMiB").GetDouble() == 512,
            "clean returns explicit final simulated result");
        var before = dispatchCount;
        using var rejected = JsonDocument.Parse(await Call("arbitrary.exe"));
        Check(!rejected.RootElement.GetProperty("Success").GetBoolean() && before == dispatchCount, "unknown command never reaches dispatch");
        for (var i = 0; i < 12; i++) await Call("status");
        Check(true, "pipe instances are reused across 12 further requests");
        server.Dispose();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));
        Check(true, "idle pipe listeners stop on disposal");
    }

    private static void TestDisplay()
    {
        var pipeName = "MemoryGuardian_UI_Test_" + Guid.NewGuid().ToString("N");
        using var form = new MainForm(true, true, pipeName);
        _ = form.Handle;
        form.PerformLayout();
        Application.DoEvents();
        var host = (MonitorHost)typeof(MainForm).GetField("host", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        var resultField = typeof(MonitorHost).GetField("lastResult", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var time = DateTimeOffset.UtcNow;
        object Result(bool success, ulong availableAfter, string message) => new
        {
            Time = time, Reason = "test", Success = success, NtStatus = 0u, Win32Error = 0, Message = message,
            DurationSeconds = 1.5,
            Before = new MemorySnapshot(time, 8UL << 30, 2UL << 30, 3UL << 30),
            After = new MemorySnapshot(time, 8UL << 30, availableAfter, 3UL << 30),
            AvailableChangeMiB = ((double)availableAfter - (2UL << 30)) / 1048576d
        };
        void SetResult(object value)
        {
            resultField.SetValue(host, JsonSerializer.Deserialize(JsonSerializer.Serialize(value), resultField.FieldType));
            typeof(MainForm).GetMethod("RefreshDisplay", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
            form.PerformLayout();
        }
        string Text() => string.Join("\n", Descendants(form).OfType<Label>().Select(label => label.Text));
        SetResult(Result(true, 3UL << 30, "工作集整理完成"));
        Check(Text().Contains("整理完成") && Text().Contains("2.00 GiB → 3.00 GiB") && Text().Contains("+1024.0 MiB"), "UI shows successful before/after and signed positive observation");
        SetResult(Result(true, 1UL << 30, "工作集整理完成"));
        Check(Text().Contains("-1024.0 MiB"), "UI preserves a negative observation");
        SetResult(Result(false, 2UL << 30, "模拟权限失败"));
        Check(Text().Contains("整理失败") && Text().Contains("模拟权限失败"), "UI clearly displays failed latest attempt");
        SetResult(new { Time = time, Reason = "test", Success = false, Message = "模拟读取失败" });
        Check(Text().Contains("模拟读取失败") && !Text().Contains("+1024.0 MiB"), "UI failure without readings does not display stale success");
        var root = form.Controls.OfType<TableLayoutPanel>().Single();
        var policy = Descendants(form).OfType<Label>().Single(label => label.Text.StartsWith("自动："));
        var policyRow = root.GetRow(policy);
        Check(root.GetRowHeights()[policyRow] >= policy.GetPreferredSize(new Size(root.DisplayRectangle.Width, 0)).Height + policy.Margin.Vertical,
            "policy row has room for both complete lines");
        Check(form.ClientSize.Width >= 700 && form.ClientSize.Height >= 520, "default client area accommodates full content");
        TestExitDrain(form, host, pipeName);
    }

    private static void TestExitDrain(MainForm form, MonitorHost host, string pipeName)
    {
        // Hold an actual response unacknowledged to reproduce an outstanding cleanup reply.
        var busyField = typeof(MonitorHost).GetField("busy", BindingFlags.Instance | BindingFlags.NonPublic)!;
        busyField.SetValue(host, true);
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        client.Connect(1200);
        client.Write(Encoding.UTF8.GetBytes("status\n"));
        client.Flush();
        using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
        var response = reader.ReadLineAsync();
        PumpUntil(() => response.IsCompleted);
        Check(response.GetAwaiter().GetResult()!.Contains("\"Busy\":true"), "actual UI control reports simulated busy state");
        var stop = LocalControl.CallAsync("stop", pipeName: pipeName);
        PumpUntil(() => stop.IsCompleted);
        using var stopped = JsonDocument.Parse(stop.GetAwaiter().GetResult());
        Check(stopped.RootElement.GetProperty("Message").GetString() == "整理完成后退出", "actual UI stop request is accepted while busy");
        busyField.SetValue(host, false);
        typeof(MainForm).GetMethod("RefreshDisplay", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
        Application.DoEvents();
        Check(!form.IsDisposed, "queued exit waits for outstanding reply acknowledgement");
        try { client.WriteByte(1); client.Flush(); } catch (IOException) { }
        PumpUntil(() => form.IsDisposed);
        Check(form.IsDisposed, "queued exit finishes after outstanding reply acknowledgement");
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var wait = Stopwatch.StartNew();
        while (!condition() && wait.Elapsed < TimeSpan.FromSeconds(5))
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
        if (!condition()) throw new TimeoutException("UI test did not complete in five seconds");
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Check(bool condition, string name)
    {
        if (condition) Interlocked.Increment(ref passed); else Interlocked.Increment(ref failed);
        Console.WriteLine((condition ? "PASS: " : "FAIL: ") + name);
    }
}
