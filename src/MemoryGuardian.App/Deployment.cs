using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace MemoryGuardian.App;

public static class Deployment
{
    public static void Install()
    {
        if (!WindowsMemory.IsAdministrator()) throw new UnauthorizedAccessException("部署需要管理员权限");
        var source = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var target = AppStorage.InstallDirectory;
        AppStorage.RejectReparsePath(source);
        AppStorage.RejectReparsePath(target);
        var manifestPath = Path.Combine(source, "package-manifest.json");
        var manifest = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(manifestPath))
            ?? throw new IOException("发布包文件清单无效");
        if (!manifest.ContainsKey("MemoryGuardian.exe") || !manifest.ContainsKey("coreclr.dll"))
            throw new IOException("发布包缺少主程序或自带运行库");
        foreach (var item in manifest) VerifyFile(SafeChild(source, item.Key), item.Value);
        AppStorage.SecureDirectory(Path.GetDirectoryName(target)!);
        AppStorage.SecureDirectory(target);
        if (!string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var item in manifest)
            {
                var destination = SafeChild(target, item.Key);
                AppStorage.RejectReparsePath(destination);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(SafeChild(source, item.Key), destination, true);
                VerifyFile(destination, item.Value);
            }
            AppStorage.RejectReparsePath(Path.Combine(target, "package-manifest.json"));
            File.Copy(manifestPath, Path.Combine(target, "package-manifest.json"), true);
        }
        var storage = new AppStorage();
        StartupRegistration.Enable(AppStorage.InstalledExe);
        if (!StartupRegistration.IsEnabled()) throw new IOException("登录启动任务验证失败");
        storage.Log("installed", new { Version = "1.0.1", Path = target, TaskName = StartupRegistration.TaskName, Files = manifest.Count });
        Process.Start(new ProcessStartInfo(AppStorage.InstalledExe, "--background")
        {
            UseShellExecute = false, WorkingDirectory = target, WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    private static string SafeChild(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':')) throw new IOException("文件清单路径无效");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("文件清单路径越界");
        return path;
    }

    private static void VerifyFile(string path, string expectedHash)
    {
        AppStorage.RejectReparsePath(path);
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("文件校验失败：" + Path.GetFileName(path));
    }
}
