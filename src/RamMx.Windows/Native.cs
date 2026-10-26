using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RamMx.Windows;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct PerformanceInfo
    {
        public uint Size;
        public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct MemoryCounters
    {
        public uint Size, PageFaultCount;
        public nuint PeakWorkingSet, WorkingSet, PeakPagedPool, PagedPool, PeakNonPagedPool, NonPagedPool, PagefileUsage, PeakPagefileUsage, PrivateUsage, PrivateWorkingSet;
        public ulong SharedCommit;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct ProtectionInfo { public uint Level; }
    [DllImport("psapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetPerformanceInfo(out PerformanceInfo info, uint size);
    [DllImport("psapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetProcessMemoryInfo(SafeProcessHandle process, ref MemoryCounters counters, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsProcessCritical(SafeProcessHandle process, [MarshalAs(UnmanagedType.Bool)] out bool critical);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetProcessInformation(SafeProcessHandle process, int infoClass, out ProtectionInfo info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetPhysicallyInstalledSystemMemory(out ulong kilobytes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern int GetPackageFullName(SafeProcessHandle process, ref uint length, StringBuilder? fullName);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    internal delegate bool EnumWindowCallback(nint window, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumWindows(EnumWindowCallback callback, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);

    internal static string ImagePath(SafeProcessHandle process)
    {
        var result = new StringBuilder(32768); uint length = (uint)result.Capacity;
        return QueryFullProcessImageName(process, 0, result, ref length) ? result.ToString() : "";
    }
    internal static string? PackageName(SafeProcessHandle process)
    {
        uint length = 0;
        if (GetPackageFullName(process, ref length, null) != 122 || length > 2048) return null;
        var value = new StringBuilder((int)length);
        return GetPackageFullName(process, ref length, value) == 0 ? value.ToString() : null;
    }
}
