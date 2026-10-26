namespace RamMx.Core;

public sealed record PolicyDecision(bool Allowed, string Reason);
public static class ActionPolicy
{
    public static PolicyDecision Evaluate(ProcessSample process)
    {
        if (process.Critical != false) return new(false, "เป็น critical process หรือยังตรวจสถานะไม่ได้");
        if (process.Protected) return new(false, "Windows ปกป้อง process นี้");
        if (process.Role != AppRole.UserApp) return new(false, "ส่วนของระบบ / runtime / ตัวตนไม่ชัดเจน: ไม่เสนอให้หยุด");
        if (process.Identity.CreationFileTime <= 0 || string.IsNullOrWhiteSpace(process.Identity.Path)) return new(false, "ข้อมูลตัวตนไม่ครบ");
        return new(true, "ปิดได้เมื่อไม่มีงานค้าง");
    }
}

public sealed class RecommendationEngine
{
    private DateTimeOffset? lowSince;
    public bool Pressure { get; private set; }
    public IReadOnlyList<Recommendation> Evaluate(MemorySnapshot snapshot, IReadOnlyList<ProcessGroup> groups, UserSettings settings)
    {
        bool low = snapshot.AvailablePercent < settings.LowAvailablePercent || snapshot.CommitPercent > settings.HighCommitPercent;
        lowSince = low ? lowSince ?? snapshot.Timestamp : null;
        Pressure = lowSince.HasValue && snapshot.Timestamp - lowSince.Value >= TimeSpan.FromSeconds(60);
        var result = new List<Recommendation>();
        var candidates = settings.ProfileCandidates.GetValueOrDefault(settings.Profile) ?? [];
        foreach (var group in groups.Where(g => candidates.Contains(g.AppKey) && !settings.KeepApps.Contains(g.AppKey) && !g.Foreground && g.CanStop)
                     .OrderByDescending(g => g.PrivateBytes))
            result.Add(new(group.AppKey, $"ตรวจการปิด {group.Name}", "คุณระบุว่าไม่ใช้ในโปรไฟล์นี้ ตรวจงานค้างก่อนปิด", $"RAM เฉพาะแอปขณะนี้ {group.RamLabel} — ไม่ใช่ยอดที่จะคืนได้แน่นอน", "close"));
        if (Pressure && result.Count == 0) result.Add(new("", "RAM พร้อมใช้ต่ำต่อเนื่อง หรือ commit ใกล้ limit", "ยังไม่มีแอปที่ผ่านเงื่อนไขให้แนะนำปิด เลือกตรวจ workload และกำหนดรายการไม่ใช้ในโปรไฟล์", "ไม่มีการปิดอัตโนมัติ", "inspect"));
        if (snapshot.DiskTotal > 0 && (double)snapshot.DiskFree / snapshot.DiskTotal < .1)
            result.Add(new("", "พื้นที่ไดรฟ์ระบบเหลือน้อย", "ตรวจแอปที่ไม่ใช้และ cache ที่ระบุเจ้าของได้", $"เหลือ {Format.Bytes(snapshot.DiskFree)}; การล้างดิสก์ไม่เพิ่ม RAM โดยตรง", "storage"));
        return result;
    }
}

public static class PathPolicy
{
    public static bool IsInside(string root, string candidate)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedCandidate = Path.GetFullPath(candidate);
        return normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    public static bool IsSameOrInside(string root, string candidate) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate)), StringComparison.OrdinalIgnoreCase) || IsInside(root, candidate);
}
