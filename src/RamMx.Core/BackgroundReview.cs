namespace RamMx.Core;

public sealed record BackgroundCandidate(string AppKey, string Name, string RamLabel, string Reason);

public static class BackgroundReview
{
    public static IReadOnlyList<BackgroundCandidate> Evaluate(IReadOnlyList<ProcessGroup> groups, BehaviourTracker tracker, DateTimeOffset now)
    {
        var result = new List<BackgroundCandidate>();
        foreach (var group in groups.Where(g => !g.Foreground && g.Members.All(p => !p.HasWindow) && g.Role is AppRole.UserApp or AppRole.Unknown or AppRole.SharedRuntime))
        {
            var reasons = new List<string>();
            if (group.Path.Length == 0) reasons.Add("ระบุตำแหน่งไฟล์ไม่ได้ อาจเป็นข้อจำกัดสิทธิ์");
            else if (group.Path.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase)) reasons.Add("ทำงานจากโฟลเดอร์ชั่วคราว ตรวจแหล่งที่มาก่อน");
            foreach (var evidence in tracker.EvidenceFor(group.AppKey, now).Where(e => e.Severity == "ควรตรวจสอบ")) reasons.Add(evidence.Detail);
            if (reasons.Count > 0) result.Add(new(group.AppKey, group.Name, group.RamLabel, string.Join(" · ", reasons)));
        }
        return result.Take(100).ToArray();
    }
}
