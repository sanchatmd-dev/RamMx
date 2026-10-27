using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using RamMx.Core;
using Windows.Management.Deployment;

namespace RamMx.Windows;

public sealed class UninstallService
{
    public async Task<ActionResult> UninstallAsync(InstalledApp expected, CancellationToken cancellationToken = default)
    {
        var actual = await Task.Run(() => new InventoryService().Read().FirstOrDefault(a => a.Key == expected.Key), cancellationToken);
        if (actual == null) return new(OperationStatus.Cancelled, "แอปไม่อยู่ใน inventory แล้ว");
        if (actual != expected) return new(OperationStatus.Cancelled, "ข้อมูลการติดตั้งเปลี่ยน โปรด refresh และยืนยันใหม่");
        if (!actual.CanUninstall) return new(OperationStatus.Failed, "ไม่รองรับการถอนรายการนี้ / เป็นส่วนประกอบที่ปกป้องไว้");
        try
        {
            int? exitCode = null;
            if (actual.Kind == "MSIX")
            {
                var operation = new PackageManager().RemovePackageAsync(actual.PackageFullName!);
                // PackageManager is the authority for data/dependency removal. Do not delete WindowsApps ourselves.
                var pending = operation.AsTask();
                DeploymentResult result;
                try { result = await WaitForNativeAsync(pending, cancellationToken); }
                catch (OperationCanceledException) { return new(OperationStatus.UnknownOutcome, "หยุดรอผลแล้ว Windows อาจยังถอน package อยู่ ตรวจรายการติดตั้งก่อนทำซ้ำ"); }
                if (result.ExtendedErrorCode != null && result.ExtendedErrorCode.HResult < 0) return new(OperationStatus.Failed, result.ErrorText, NativeCode: result.ExtendedErrorCode.HResult);
            }
            else
            {
                ProcessStartInfo start;
                if (actual.Kind == "MSI" && Guid.TryParse(actual.ProductCode, out var code))
                {
                    start = new(Path.Combine(Environment.SystemDirectory, "msiexec.exe")) { UseShellExecute = true };
                    start.ArgumentList.Add("/x"); start.ArgumentList.Add("{" + code.ToString() + "}"); start.ArgumentList.Add("/norestart");
                }
                else
                {
                    var parsed = ParseCommand(actual.UninstallCommand);
                    if (!File.Exists(parsed.Executable)) return new(OperationStatus.Failed, "ไม่พบตัวถอนติดตั้ง ใช้ Windows Installed apps ตรวจต่อ");
                    var ext = Path.GetExtension(parsed.Executable);
                    if (!ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)) return new(OperationStatus.Failed, "ตัวถอนติดตั้งไม่ใช่ executable ที่รองรับ");
                    if (new[] { "cmd", "powershell", "pwsh", "wscript", "cscript", "rundll32", "regsvr32", "mshta" }.Contains(Path.GetFileNameWithoutExtension(parsed.Executable), StringComparer.OrdinalIgnoreCase))
                        return new(OperationStatus.Failed, "คำสั่งถอนผ่าน script/system host ต้องใช้ Windows Installed apps");
                    start = new(parsed.Executable) { UseShellExecute = true, Arguments = parsed.Arguments, WorkingDirectory = Path.GetDirectoryName(parsed.Executable)! };
                    // Never add runas to a per-user registry command. Vendor manifest/MSI controls its own elevation.
                }
                using var process = Process.Start(start);
                if (process == null) return new(OperationStatus.UnknownOutcome, "เปิดตัวถอนติดตั้งแล้วแต่ติดตาม process ไม่ได้ ตรวจ inventory อีกครั้ง");
                try { await process.WaitForExitAsync(cancellationToken); }
                catch (OperationCanceledException) { return new(OperationStatus.UnknownOutcome, "หยุดรอผลแล้ว ตัวถอนติดตั้งอาจยังทำงานอยู่ — ไม่ได้ force stop"); }
                exitCode = process.ExitCode;
                if (exitCode == 1602) return new(OperationStatus.Cancelled, "ผู้ใช้ยกเลิกในตัวถอนติดตั้ง", NativeCode: exitCode);
                if (exitCode is 3010 or 1641) return new(OperationStatus.RebootRequired, "ตัวถอนติดตั้งแจ้งว่าต้อง restart กรุณาบันทึกงานก่อน restart ด้วยตนเอง", NativeCode: exitCode);
            }
            var remains = await Task.Run(() => new InventoryService().Read().Any(a => a.Key == actual.Key));
            return remains ? new(OperationStatus.UnknownOutcome, $"ตัวถอนจบแล้ว แต่ยังพบ registration (exit {exitCode?.ToString() ?? "MSIX"}) อาจมี child process หรือรอ restart ตรวจใหม่ก่อนล้าง leftovers", NativeCode: exitCode)
                : new(OperationStatus.Completed, "ไม่พบ registration หลังถอนแล้ว เลือกตรวจ cache ตกค้างต่อได้ ข้อมูลส่วนตัวและไฟล์ที่ไม่รู้เจ้าของไม่ได้ถูกลบโดย RamMx", NativeCode: exitCode);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return new(OperationStatus.Cancelled, "ยกเลิกการอนุญาตของ Windows"); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or COMException or ArgumentException)
        { return new(OperationStatus.Failed, ex.Message, NativeCode: ex.HResult); }
    }

    internal static async Task<T> WaitForNativeAsync<T>(Task<T> operation, CancellationToken token)
    {
        // Cancellation stops this app's wait, not the OS operation or its possible data removal.
        try { return await operation.WaitAsync(token); }
        catch (OperationCanceledException)
        {
            _ = operation.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    public static (string Executable, string Arguments) ParseCommand(string command)
    {
        command = Environment.ExpandEnvironmentVariables(command).Trim();
        if (command.Length == 0 || command.Contains('\n') || command.Contains('\r')) throw new ArgumentException("Uninstall command ไม่ถูกต้อง");
        if (command[0] == '"')
        {
            int end = command.IndexOf('"', 1);
            if (end < 2) throw new ArgumentException("Uninstall command มี quoting ไม่ครบ");
            var executable = command[1..end];
            if (!Path.IsPathFullyQualified(executable)) throw new ArgumentException("ต้องเป็น absolute executable path");
            return (executable, command[(end + 1)..].TrimStart());
        }
        // Unquoted paths containing spaces are ambiguous. Do not search progressively for an executable.
        var firstSpace = command.IndexOf(' ');
        var path = firstSpace < 0 ? command : command[..firstSpace];
        if (!Path.IsPathFullyQualified(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Uninstall command คลุมเครือ เปิด Windows Installed apps เพื่อถอนรายการนี้");
        return (path, firstSpace < 0 ? "" : command[(firstSpace + 1)..]);
    }
}
