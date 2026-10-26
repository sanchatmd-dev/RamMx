namespace RamMx.Core;

public sealed class BehaviourTracker
{
    private readonly Dictionary<string, Queue<(DateTimeOffset Time, ulong Bytes)>> history = new();
    public void Observe(DateTimeOffset now, IReadOnlyList<ProcessGroup> groups)
    {
        var live = groups.Select(g => g.AppKey).ToHashSet();
        foreach (var key in history.Keys.Where(k => !live.Contains(k)).ToArray()) history.Remove(key);
        foreach (var group in groups.Where(g => g.Coverage == g.Count))
        {
            if (!history.TryGetValue(group.AppKey, out var queue)) history[group.AppKey] = queue = new();
            if (queue.Count == 0 || now - queue.Last().Time >= TimeSpan.FromSeconds(10)) queue.Enqueue((now, group.PrivateBytes));
            while (queue.Count > 61 || (queue.Count > 0 && now - queue.Peek().Time > TimeSpan.FromMinutes(10))) queue.Dequeue();
        }
    }
    public IReadOnlyList<Evidence> EvidenceFor(string appKey, DateTimeOffset now)
    {
        if (!history.TryGetValue(appKey, out var queue) || queue.Count < 2 || now - queue.Peek().Time < TimeSpan.FromMinutes(5))
            return [new("กำลังเก็บ baseline", "ต้องมีข้อมูลอย่างน้อย 5 นาทีจึงประเมินการเติบโตของ RAM", "ข้อมูลไม่ครบ", now)];
        var first = queue.Peek().Bytes; var last = queue.Last().Bytes;
        if (last > first + 128UL * 1024 * 1024 && last > first * 1.5)
            return [new("RAM เพิ่มขึ้นระหว่างที่ติดตาม", $"จาก {Format.Bytes(first)} เป็น {Format.Bytes(last)} อาจเป็น workload/cache ปกติหรือควรตรวจ leak; ไม่ใช่หลักฐาน malware", "ควรตรวจสอบ", now)];
        return [new("แนวโน้ม RAM", "ยังไม่พบการเพิ่มเกินเกณฑ์ในช่วงที่สังเกต ไม่ใช่การรับรองความปลอดภัย", "ข้อมูลทรัพยากร", now)];
    }
}
