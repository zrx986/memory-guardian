using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace MemoryGuardian.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var argument = args.Length == 0 ? "" : args[0];
        try
        {
            if (args.Length > 1) throw new ArgumentException("一次只接受一个命令");
            if (argument is "--help" or "-h")
            {
                Print("说的道理 · 内存助手 1.0.1\n--probe 只读诊断\n--status 当前状态\n--clean 手动整理一次\n--pause 暂停自动整理\n--resume 继续自动整理\n--show 打开窗口\n--stop 退出进程\n--install 部署并启用登录启动\n--background 托盘启动\n--monitor-only 只读验证（不会清理或修改自启）");
                return 0;
            }
            if (argument == "--probe")
            {
                Print(JsonSerializer.Serialize(new
                {
                    Success = true, Memory = WindowsMemory.Read(), Elevated = WindowsMemory.IsAdministrator(),
                    Runtime = RuntimeInformation.FrameworkDescription,
                    RuntimeDirectory = RuntimeEnvironment.GetRuntimeDirectory(),
                    FullScreen = WindowsMemory.IsFullScreenForeground(), CleanupExecuted = false
                }));
                return 0;
            }
            if (argument is "--status" or "--clean" or "--pause" or "--resume" or "--stop" or "--show")
            {
                try
                {
                    var reply = LocalControl.CallAsync(argument[2..]).GetAwaiter().GetResult();
                    Print(reply);
                    using var document = JsonDocument.Parse(reply);
                    return document.RootElement.TryGetProperty("Success", out var success) && success.ValueKind == JsonValueKind.False ? 1 : 0;
                }
                catch (TimeoutException)
                {
                    Print(JsonSerializer.Serialize(new { Running = (bool?)null, Success = false, Code = "connection-timeout", Message = "内存助手未运行或控制入口暂时繁忙，请稍后重试", StateFile = AppStorage.StateFile }));
                    return 2;
                }
            }
            if (argument is not ("" or "--install" or "--background" or "--monitor-only"))
                throw new ArgumentException("未知命令；使用 --help 查看用法");
            var readOnly = argument == "--monitor-only";
            if (argument == "")
            {
                try { LocalControl.CallAsync("show", 350).GetAwaiter().GetResult(); return 0; }
                catch (TimeoutException) { }
            }
            if (!readOnly && !WindowsMemory.IsAdministrator()) return RelaunchElevated(argument);
            var installed = string.Equals(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar), AppStorage.InstallDirectory, StringComparison.OrdinalIgnoreCase);
            if (!readOnly && (argument == "--install" || !installed))
            {
                Deployment.Install();
                return 0;
            }
            using var mutex = new Mutex(true, "Global\\MemoryGuardian_" + AppStorage.UserSid, out var first);
            if (!first)
            {
                LocalControl.CallAsync("show").GetAwaiter().GetResult();
                return 0;
            }
            try
            {
                ApplicationConfiguration.Initialize();
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                using var form = new MainForm(argument is "--background" or "--monitor-only", readOnly);
                Application.Run(form);
                return 0;
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch (Exception e)
        {
            Print(JsonSerializer.Serialize(new { Success = false, Error = e.Message }));
            if (argument is "" or "--install" or "--background")
                MessageBox.Show("内存助手未能启动：\n" + e.Message, "说的道理 · 内存助手", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static int RelaunchElevated(string argument)
    {
        var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, argument)
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = AppContext.BaseDirectory
        }) ?? throw new IOException("管理员启动失败");
        if (argument == "--install") { process.WaitForExit(); return process.ExitCode; }
        return 0;
    }

    private static void Print(string text)
    {
        var handle = GetStdHandle(-11);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) AttachConsole(uint.MaxValue);
        using var writer = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        writer.WriteLine(text);
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AttachConsole(uint processId);
}
