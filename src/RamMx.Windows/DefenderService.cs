using System.Diagnostics;
using RamMx.Core;

namespace RamMx.Windows;

public static class DefenderService
{
    public static async Task<ActionResult> ScanAsync(ProcessSample target, CancellationToken token)
    {
        if (!File.Exists(target.Identity.Path)) return new(OperationStatus.Failed, "ไม่พบไฟล์เป้าหมาย");
        var platform = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows Defender", "Platform");
        var candidates = Directory.Exists(platform) ? Directory.GetDirectories(platform).OrderDescending().Select(d => Path.Combine(d, "MpCmdRun.exe")).ToList() : [];
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe"));
        var executable = candidates.FirstOrDefault(File.Exists);
        if (executable == null) return new(OperationStatus.Failed, "ไม่พบ Defender CLI เปิด Windows Security เพื่อตรวจ provider ที่ใช้งานอยู่");
        var inspection = new SignatureService().Inspect("Microsoft Defender", executable);
        if (inspection.Signature != SignatureState.Valid || !inspection.Publisher.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) return new(OperationStatus.Failed, "ยืนยันตัวตน Defender CLI ไม่ได้");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-Scan", "-ScanType", "3", "-File", target.Identity.Path }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(token); var error = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        var combined = (await output) + "\n" + (await error);
        if (combined.Length > 12000) combined = combined[..12000];
        return new(process.ExitCode == 0 ? OperationStatus.Completed : OperationStatus.Partial,
            $"Defender จบการทำงาน (exit {process.ExitCode}) โปรดตรวจ Protection history; ผลนี้ไม่ได้แปลว่าไม่มี malware ทุกกรณี", [combined], process.ExitCode);
    }
}
