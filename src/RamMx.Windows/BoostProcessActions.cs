using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using RamMx.Core;

namespace RamMx.Windows;

internal sealed class StaleBoostTargetException : Exception { public StaleBoostTargetException() : base("Process เดิมจบหรือเปลี่ยนตัวตนแล้ว") { } }

internal static class BoostProcessActions
{
    internal const uint LowPriority = 2;
    internal static SafeProcessHandle Open(ProcessIdentity identity, bool trim = false)
    {
        if (identity.Pid == Environment.ProcessId || identity.CreationFileTime <= 0 || string.IsNullOrWhiteSpace(identity.Path))
            throw new InvalidOperationException("ตัวตนเป้าหมายไม่ครบหรือเป็น RamMx");
        var handle = Native.OpenProcess(0x1000 | 0x200u | (trim ? 0x100u : 0u), false, identity.Pid);
        try
        {
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 87) throw new StaleBoostTargetException();
                throw new Win32Exception(error);
            }
            if (!Native.GetProcessTimes(handle, out var created, out _, out _, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var path = Native.ImagePath(handle);
            if (path.Length == 0) throw new InvalidOperationException("อ่านตัวตนปัจจุบันไม่ได้");
            if (created != identity.CreationFileTime || !path.Equals(identity.Path, StringComparison.OrdinalIgnoreCase)) throw new StaleBoostTargetException();
            if (PathPolicy.IsInside(Environment.GetFolderPath(Environment.SpecialFolder.Windows), path) || path.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("ไม่เปลี่ยน memory priority ของไฟล์ระบบหรือเส้นทางชั่วคราว");
            if (!Native.IsProcessCritical(handle, out var critical) || critical || !Native.GetProcessInformation(handle, 7, out var protection, 4) || protection.Level != 0xfffffffe)
                throw new InvalidOperationException("ไม่ผ่านการตรวจ critical/protected status");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }
    internal static uint ReadPriority(SafeProcessHandle handle)
    {
        if (!Native.GetMemoryPriority(handle, 0, out var value, 4)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (value.Priority is < 1 or > 5) throw new InvalidOperationException("Memory priority ไม่อยู่ในช่วงที่รองรับ");
        return value.Priority;
    }
    internal static void SetPriority(SafeProcessHandle handle, uint priority)
    {
        if (priority is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(priority));
        var info = new Native.MemoryPriorityInfo { Priority = priority };
        if (!Native.SetMemoryPriority(handle, 0, ref info, 4)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    internal static bool IsForeground(IReadOnlySet<int> appPids)
    {
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out var pid);
        return appPids.Contains((int)pid);
    }
    internal static void Trim(SafeProcessHandle handle)
    {
        if (!Native.EmptyWorkingSet(handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
}
