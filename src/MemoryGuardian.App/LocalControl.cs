using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace MemoryGuardian.App;

public sealed class LocalControl : IDisposable
{
    private static string PipeName => "MemoryGuardian_" + AppStorage.UserSid;
    private readonly CancellationTokenSource cancel = new();
    private const int ConnectionLimit = 4;
    private readonly NamedPipeServerStream[] pipes;
    private readonly Func<string, Task<object>> dispatch;
    private Task? running;
    private int pendingRequests, disposed;
    public bool HasPendingRequests => Volatile.Read(ref pendingRequests) > 0;
    public event Action? RequestsDrained;
    public LocalControl(Func<string, Task<object>> dispatch, string? pipeName = null)
    {
        this.dispatch = dispatch;
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(AppStorage.UserSid), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            // An unelevated read-only host must be able to create its own remaining instances.
            // Elevated hosts keep the user's existing ReadWrite grant only.
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(AppStorage.UserSid), PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        }
        pipes = new NamedPipeServerStream[ConnectionLimit];
        try
        {
            for (var i = 0; i < pipes.Length; i++)
                pipes[i] = NamedPipeServerStreamAcl.Create(pipeName ?? PipeName, PipeDirection.InOut, ConnectionLimit,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | (i == 0 ? PipeOptions.FirstPipeInstance : PipeOptions.None),
                    1024, 8192, security);
        }
        catch
        {
            foreach (var created in pipes) created?.Dispose();
            cancel.Dispose();
            throw;
        }
    }

    public Task RunAsync() => running ??= Task.WhenAll(pipes.Select(RunConnectionAsync));

    private async Task RunConnectionAsync(NamedPipeServerStream pipe)
    {
        while (!cancel.IsCancellationRequested)
        {
            var connected = false;
            try
            {
                await pipe.WaitForConnectionAsync(cancel.Token);
                Interlocked.Increment(ref pendingRequests);
                connected = true;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var bytes = new byte[64];
                var count = 0;
                while (count < bytes.Length)
                {
                    var read = await pipe.ReadAsync(bytes.AsMemory(count, 1), timeout.Token);
                    if (read == 0 || bytes[count] == '\n') break;
                    count += read;
                }
                var command = Encoding.UTF8.GetString(bytes, 0, count).Trim();
                object result = command is "status" or "clean" or "pause" or "resume" or "stop" or "show"
                    ? await dispatch(command)
                    : new { Success = false, Message = "不支持的控制命令" };
                var reply = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(result) + "\n");
                await pipe.WriteAsync(reply, cancel.Token);
                await pipe.FlushAsync(cancel.Token);
                // Disconnect can discard unread output. Keep the connection until the client acknowledges it.
                using var acknowledgementTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
                acknowledgementTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                await pipe.ReadExactlyAsync(new byte[1], acknowledgementTimeout.Token);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
            finally
            {
                try { if (pipe.IsConnected) pipe.Disconnect(); } catch (ObjectDisposedException) { }
                if (connected && Interlocked.Decrement(ref pendingRequests) == 0) RequestsDrained?.Invoke();
            }
        }
    }

    public static async Task<string> CallAsync(string command, int connectMilliseconds = 1200, string? pipeName = null)
    {
        using var client = new NamedPipeClientStream(".", pipeName ?? PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await client.ConnectAsync(connectMilliseconds, timeout.Token);
        var request = Encoding.UTF8.GetBytes(command + "\n");
        await client.WriteAsync(request, timeout.Token);
        await client.FlushAsync(timeout.Token);
        using var reader = new StreamReader(client, Encoding.UTF8);
        var response = await reader.ReadLineAsync(timeout.Token) ?? throw new IOException("程序没有返回状态");
        await client.WriteAsync(new byte[] { 1 }, timeout.Token);
        await client.FlushAsync(timeout.Token);
        return response;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        cancel.Cancel();
        foreach (var pipe in pipes) pipe.Dispose();
        // Listeners can still observe cancellation while their asynchronous reads unwind.
        if (running is null) cancel.Dispose();
        else _ = running.ContinueWith(_ => cancel.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
