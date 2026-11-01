using System.Runtime.InteropServices;

namespace MemoryGuardian.App;

public static class StartupRegistration
{
    public static string TaskName => "MemoryGuardian-" + AppStorage.UserSid;
    private const string Marker = "MemoryGuardian local memory assistant v1";

    private static dynamic Connect()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("无法访问 Windows 任务计划程序");
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service;
    }

    public static bool IsEnabled()
    {
        dynamic service = Connect();
        try
        {
            dynamic task = service.GetFolder("\\").GetTask(TaskName);
            return task.Enabled && (string)task.Definition.RegistrationInfo.Description == Marker &&
                string.Equals((string)task.Definition.Actions.Item(1).Path, AppStorage.InstalledExe, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e.HResult == unchecked((int)0x80070002)) { return false; }
        finally { Marshal.FinalReleaseComObject(service); }
    }

    public static void Enable(string exePath)
    {
        if (!WindowsMemory.IsAdministrator()) throw new UnauthorizedAccessException("设置自启需要管理员权限");
        if (!string.Equals(Path.GetFullPath(exePath), AppStorage.InstalledExe, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("自启只允许指向已部署的受保护程序");
        AppStorage.RejectReparsePath(exePath);
        dynamic service = Connect();
        try
        {
            dynamic folder = service.GetFolder("\\");
            EnsureOwned(folder);
            dynamic definition = service.NewTask(0);
            definition.RegistrationInfo.Description = Marker;
            definition.RegistrationInfo.Author = "LocalMemoryTools";
            definition.Principal.UserId = AppStorage.UserSid;
            definition.Principal.LogonType = 3;
            definition.Principal.RunLevel = 1;
            definition.Settings.Enabled = true;
            definition.Settings.Hidden = false;
            definition.Settings.StartWhenAvailable = true;
            definition.Settings.DisallowStartIfOnBatteries = false;
            definition.Settings.StopIfGoingOnBatteries = false;
            definition.Settings.ExecutionTimeLimit = "PT0S";
            definition.Settings.MultipleInstances = 2;
            definition.Settings.IdleSettings.StopOnIdleEnd = false;
            dynamic trigger = definition.Triggers.Create(9);
            trigger.UserId = AppStorage.UserSid;
            trigger.Delay = "PT30S";
            dynamic action = definition.Actions.Create(0);
            action.Path = exePath;
            action.Arguments = "--background";
            action.WorkingDirectory = Path.GetDirectoryName(exePath);
            folder.RegisterTaskDefinition(TaskName, definition, 6, AppStorage.UserSid, null, 3, null);
        }
        finally { Marshal.FinalReleaseComObject(service); }
    }

    public static void Disable()
    {
        dynamic service = Connect();
        try
        {
            dynamic folder = service.GetFolder("\\");
            if (EnsureOwned(folder)) folder.DeleteTask(TaskName, 0);
        }
        finally { Marshal.FinalReleaseComObject(service); }
    }

    private static bool EnsureOwned(dynamic folder)
    {
        try
        {
            dynamic task = folder.GetTask(TaskName);
            if ((string)task.Definition.RegistrationInfo.Description != Marker)
                throw new InvalidOperationException("同名自启任务并非本工具创建，已停止修改");
            return true;
        }
        catch (Exception e) when (e.HResult == unchecked((int)0x80070002)) { return false; }
    }
}
