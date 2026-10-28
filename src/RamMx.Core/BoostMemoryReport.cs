namespace RamMx.Core;

public sealed record BoostProcessMemory(ProcessIdentity Identity, string Name, ulong? Before, ulong? After);

// Only exact process identities actually acted on contribute. Exited/unreadable
// targets are unavailable, never zero; shared pages and machine totals are excluded.
public sealed record BoostMemoryReport(IReadOnlyList<BoostProcessMemory> Processes)
{
    public static BoostMemoryReport Capture(IReadOnlyList<ProcessSample> before, IReadOnlyList<ProcessSample> after,
        IReadOnlyCollection<ProcessIdentity> actedOn) => new(actedOn.Distinct().Select(id =>
        {
            var old = before.FirstOrDefault(p => p.Identity == id);
            var current = after.FirstOrDefault(p => p.Identity == id);
            return new BoostProcessMemory(id, old?.DisplayName ?? id.Pid.ToString(), old?.PrivateWorkingSet, current?.PrivateWorkingSet);
        }).ToArray());

    public string Summary
    {
        get
        {
            if (Processes.Count == 0) return "ไม่มีโปรเซสที่ดำเนินการ จึงไม่มีผล RAM ให้เปรียบเทียบ";
            var measured = Processes.Where(p => p.Before.HasValue && p.After.HasValue).ToArray();
            if (measured.Length == 0) return "อ่าน RAM เฉพาะเป้าหมายก่อน–หลังไม่ได้ ไม่ถือว่าเป็น 0 MiB";
            decimal before = measured.Sum(p => (decimal)p.Before!.Value), after = measured.Sum(p => (decimal)p.After!.Value);
            var delta = (before - after) / 1048576;
            string result = delta > 0 ? $"ลดลง {delta:F2} MiB" : delta < 0 ? $"เพิ่มขึ้น {-delta:F2} MiB" : "ยังไม่พบ RAM ลดลง (0.00 MiB)";
            return $"RAM เฉพาะเป้าหมายที่อ่านครบ {measured.Length}/{Processes.Count} process: {before / 1048576:F2} → {after / 1048576:F2} MiB · {result}\nเป็นผลสังเกตหลังดำเนินการ RAM อาจเพิ่มกลับเมื่อแอปใช้งาน; ไม่ใช่ยอด RAM ว่างที่รับประกัน";
        }
    }
}
