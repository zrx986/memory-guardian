using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MemoryGuardian.App;

public sealed record MemorySnapshot(DateTimeOffset Time, ulong TotalBytes, ulong AvailableBytes, ulong CommitBytes)
{
    public double UsedPercent => TotalBytes == 0 ? 0 : 100d * (TotalBytes - Math.Min(TotalBytes, AvailableBytes)) / TotalBytes;
}

public sealed record TrimResult(bool Success, uint NtStatus, int Win32Error, string Message);

public static class WindowsMemory
{
    public static MemorySnapshot Read()
    {
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref memory)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var performance = new PerformanceInfo { Size = (uint)Marshal.SizeOf<PerformanceInfo>() };
        if (!GetPerformanceInfo(ref performance, performance.Size)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(DateTimeOffset.UtcNow, memory.TotalPhysical, memory.AvailablePhysical,
            checked(performance.CommitTotal.ToUInt64() * performance.PageSize.ToUInt64()));
    }

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static bool IsFullScreenForeground()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || IsIconic(window)) return false;
        var className = new StringBuilder(256);
        GetClassName(window, className, className.Capacity);
        if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        if (!GetWindowRect(window, out var rect)) return false;
        var monitor = MonitorFromWindow(window, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return false;
        return rect.Left <= info.Monitor.Left && rect.Top <= info.Monitor.Top &&
            rect.Right >= info.Monitor.Right && rect.Bottom >= info.Monitor.Bottom;
    }

    public static TrimResult TrimWorkingSets()
    {
        if (!IsAdministrator()) return new(false, 0xC0000061, 1314, "需要管理员权限；自动整理已暂停");
        if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out var token)) return Win32Failure();
        using (token)
        {
            if (!LookupPrivilegeValue(null, "SeProfileSingleProcessPrivilege", out var privilege)) return Win32Failure();
            var requested = new TokenPrivileges { Count = 1, Luid = privilege, Attributes = 2 };
            if (!AdjustTokenPrivileges(token, false, ref requested, (uint)Marshal.SizeOf<TokenPrivileges>(), out var previous, out _))
                return Win32Failure();
            var adjustError = Marshal.GetLastWin32Error();
            if (adjustError != 0) return new(false, 0xC0000061, adjustError, new Win32Exception(adjustError).Message);
            TrimResult result;
            try
            {
                var operation = 2; // MemoryEmptyWorkingSets; same fixed operation verified in WMC 3.0.8.
                var status = NtSetSystemInformation(80, ref operation, sizeof(int));
                var error = status == 0 ? 0 : unchecked((int)RtlNtStatusToDosError(status));
                result = status == 0
                    ? new(true, 0, 0, "工作集整理完成")
                    : new(false, status, error, $"整理失败：{new Win32Exception(error).Message} (NTSTATUS 0x{status:X8})");
            }
            finally
            {
                var restored = RestoreTokenPrivileges(token, false, ref previous, 0, IntPtr.Zero, IntPtr.Zero);
                var restoreError = Marshal.GetLastWin32Error();
                if (!restored || restoreError != 0)
                    throw new Win32Exception(restoreError, "无法恢复进程权限，程序应停止自动整理");
            }
            return result;
        }
    }

    private static TrimResult Win32Failure()
    {
        var error = Marshal.GetLastWin32Error();
        return new(false, 0xC0000001, error, new Win32Exception(error).Message);
    }

    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PerformanceInfo
    {
        public uint Size;
        public UIntPtr CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)] private struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }

    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("psapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetPerformanceInfo(ref PerformanceInfo info, uint size);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivileges state, uint length, out TokenPrivileges previous, out uint required);
    [DllImport("advapi32.dll", EntryPoint = "AdjustTokenPrivileges", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RestoreTokenPrivileges(SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivileges state, uint length, IntPtr previous, IntPtr required);
    [DllImport("ntdll.dll")] private static extern uint NtSetSystemInformation(int informationClass, ref int information, int length);
    [DllImport("ntdll.dll")] private static extern uint RtlNtStatusToDosError(uint status);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int max);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
