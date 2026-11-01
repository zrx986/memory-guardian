using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace MemoryGuardian.App;

public sealed class AppSettings
{
    public bool Paused { get; set; }
    public DateTimeOffset? LastAttemptUtc { get; set; }
    public bool PressureCycleConsumed { get; set; }
}

public sealed class AppStorage
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static string UserSid { get; } = ReadUserSid();
    public static string BaseDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LocalMemoryTools", "MemoryGuardian", UserSid);
    public static string StateFile => Path.Combine(BaseDirectory, "status.json");
    public static string InstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LocalMemoryTools", "MemoryGuardian");
    public static string InstalledExe => Path.Combine(InstallDirectory, "MemoryGuardian.exe");
    private readonly object gate = new();
    private readonly bool memoryOnly;

    private static string ReadUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!.Value;
    }

    public AppStorage(bool readOnly = false)
    {
        memoryOnly = readOnly;
        if (memoryOnly) return;
        SecureDirectory(Path.GetDirectoryName(Path.GetDirectoryName(BaseDirectory)!)!);
        SecureDirectory(Path.GetDirectoryName(BaseDirectory)!);
        SecureDirectory(BaseDirectory);
    }

    public static void RejectReparsePath(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("不支持链接或重解析路径：" + current);
            current = Path.GetDirectoryName(current);
        }
    }

    public static void SecureDirectory(string path)
    {
        RejectReparsePath(path);
        Directory.CreateDirectory(path);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        security.SetOwner(admins);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    public AppSettings Load()
    {
        if (memoryOnly) return new();
        var path = Path.Combine(BaseDirectory, "settings.json");
        RejectReparsePath(path);
        if (!File.Exists(path)) return new();
        return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? throw new IOException("配置文件无效，请检查记录");
    }

    public void Save(AppSettings settings) => Write("settings.json", settings);
    public void WriteState(object state) => Write("status.json", state);
    private void Write(string name, object value)
    {
        if (memoryOnly) return;
        lock (gate)
        {
            var path = Path.Combine(BaseDirectory, name);
            RejectReparsePath(path);
            var temp = path + ".new";
            RejectReparsePath(temp);
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Json), new UTF8Encoding(false));
            File.Move(temp, path, true);
        }
    }

    public void Log(string kind, object data)
    {
        if (memoryOnly) return;
        lock (gate)
        {
            var path = Path.Combine(BaseDirectory, "events.jsonl");
            RejectReparsePath(path);
            if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
            {
                var old = path + ".1";
                RejectReparsePath(old);
                File.Move(path, old, true);
            }
            File.AppendAllText(path, JsonSerializer.Serialize(new { Time = DateTimeOffset.UtcNow, Kind = kind, Data = data }) + Environment.NewLine, new UTF8Encoding(false));
        }
    }
}
