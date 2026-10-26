using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using RamMx.Core;

namespace RamMx.Windows;

public sealed class ProcessActions
{
    public async Task<ActionResult> ExecuteAsync(IReadOnlyList<ProcessSample> targets, bool force, CancellationToken cancellationToken = default)
    {
        if (targets.Count is 0 or > 200) return new(OperationStatus.Failed, "จำนวนเป้าหมายไม่ถูกต้อง");
        // Recollect all metadata before acting; caller-supplied role/critical flags are never trusted.
        var current = new TelemetryService().ReadProcesses().ToDictionary(p => p.Identity.InstanceKey);
        foreach (var target in targets)
        {
            if (!current.TryGetValue(target.Identity.InstanceKey, out var actual) || !actual.Identity.Path.Equals(target.Identity.Path, StringComparison.OrdinalIgnoreCase))
                return new(OperationStatus.Cancelled, "เป้าหมายเปลี่ยนหรือจบไปแล้ว โปรด refresh และเลือกใหม่");
            var policy = ActionPolicy.Evaluate(actual);
            if (!policy.Allowed) return new(OperationStatus.Failed, policy.Reason);
            if (actual.Identity.Pid == Environment.ProcessId) return new(OperationStatus.Failed, "ไม่อนุญาตหยุด RamMx");
        }
        var details = new List<string>(); int succeeded = 0;
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var handle = Native.OpenProcess(0x1000 | 0x100000u | (force ? 1u : 0u), false, target.Identity.Pid);
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!Native.GetProcessTimes(handle, out var created, out _, out _, out _) || created != target.Identity.CreationFileTime || !Native.ImagePath(handle).Equals(target.Identity.Path, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Process identity เปลี่ยนก่อนดำเนินการ");
                if (!Native.IsProcessCritical(handle, out var critical) || critical) throw new InvalidOperationException("ไม่ผ่านการตรวจ critical status");
                if (Native.GetProcessInformation(handle, 7, out var protect, 4) && protect.Level != 0xfffffffe) throw new InvalidOperationException("Protected process");
                if (force)
                {
                    if (!Native.TerminateProcess(handle, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                else
                {
                    using var process = Process.GetProcessById(target.Identity.Pid);
                    var window = process.MainWindowHandle;
                    if (window == 0) { details.Add($"{target.Name} ({target.Identity.Pid}): ไม่มีหน้าต่างให้ขอปิด; ไม่ force อัตโนมัติ"); continue; }
                    Native.GetWindowThreadProcessId(window, out uint windowPid);
                    if (windowPid != target.Identity.Pid || !Native.PostMessage(window, 0x10, 0, 0)) throw new InvalidOperationException("ส่งคำขอปิดหน้าต่างไม่ได้");
                }
                var result = await Task.Run(() => Native.WaitForSingleObject(handle, force ? 3000u : 1500u), cancellationToken);
                if (result == 0) { succeeded++; details.Add($"{target.Name} ({target.Identity.Pid}): จบแล้ว"); }
                else details.Add($"{target.Name} ({target.Identity.Pid}): ยังทำงานอยู่ / อาจรอการบันทึกข้อมูล");
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
            { details.Add($"{target.Name} ({target.Identity.Pid}): {ex.Message}"); }
        }
        return new(succeeded == targets.Count ? OperationStatus.Completed : OperationStatus.Partial, $"ยืนยันว่าจบแล้ว {succeeded}/{targets.Count} process", details);
    }
}
