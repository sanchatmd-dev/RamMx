using System.ComponentModel;
using RamMx.Core;

namespace RamMx.Windows;

// Serialized independently from UI polling. Stored entries are restore intent, never authority to reapply Boost.
public sealed class BoostService
{
    private readonly object gate = new();
    private readonly IBoostJournal journal;
    private readonly Func<IReadOnlyList<ProcessSample>> readProcesses;
    private readonly BoostActivityTracker activity = new();
    private readonly Func<DateTimeOffset> clock;
    private readonly Dictionary<string, BoostEntry> pending;
    private DateTimeOffset lastRestoreAttempt;
    public BoostService(IBoostJournal journal, Func<IReadOnlyList<ProcessSample>>? readProcesses = null)
        : this(journal, readProcesses, () => DateTimeOffset.UtcNow) { }
    internal BoostService(IBoostJournal journal, Func<IReadOnlyList<ProcessSample>>? readProcesses, Func<DateTimeOffset> clock)
    {
        this.clock = clock;
        this.journal = journal; this.readProcesses = readProcesses ?? (() => new TelemetryService().ReadProcesses());
        pending = journal.PendingBoost().ToDictionary(e => e.Identity.InstanceKey);
    }
    public int PendingCount { get { lock (gate) return pending.Count; } }
    public IReadOnlyList<BoostAssessment> Review(IReadOnlyList<ProcessSample> samples, UserSettings settings, DateTimeOffset now)
    { lock (gate) return BoostPolicy.Assess(samples, settings, activity, now); }
    public IReadOnlyList<BoostAssessment> Observe(IReadOnlyList<ProcessSample> samples, UserSettings settings, DateTimeOffset now)
    {
        lock (gate)
        {
            activity.Observe(now, samples);
            if (pending.Count == 0) return [];
            var plan = BoostPolicy.Assess(samples, settings, activity, now);
            // Restore when an app becomes active, is protected/removed, or the profile changes.
            if (pending.Count > 0 && now - lastRestoreAttempt >= TimeSpan.FromSeconds(2))
            {
                lastRestoreAttempt = now;
                foreach (var entry in pending.Values.ToArray())
                    if (entry.Profile != settings.Profile || !plan.Any(a => a.Group.AppKey == entry.AppKey && a.Eligible && a.Group.Members.Any(p => p.Identity == entry.Identity)))
                        RestoreOne(entry);
            }
            return plan;
        }
    }
    public ActionResult Start(IReadOnlyList<ProcessGroup> selected, UserSettings settings, bool trim, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (selected.Count == 0 || selected.Sum(g => g.Count) > 100 || pending.Count + selected.Sum(g => g.Count) > 200)
                return new(OperationStatus.Cancelled, "ไม่มีเป้าหมายหรือจำนวนเกินขีดจำกัด");
            var fresh = readProcesses(); var now = clock();
            activity.Observe(now, fresh);
            var plan = BoostPolicy.Assess(fresh, settings, activity, now);
            var details = new List<string>(); int changed = 0, trimmed = 0;
            foreach (var requested in selected)
            {
                if (cancellationToken.IsCancellationRequested) { details.Add("ยกเลิกเป้าหมายที่เหลือ; ค่าที่เปลี่ยนแล้วคืนได้ด้วยปุ่มจบโหมด"); break; }
                var actual = plan.FirstOrDefault(a => a.Group.AppKey == requested.AppKey);
                if (actual?.Eligible != true || !actual.Group.Members.Select(p => p.Identity).ToHashSet().SetEquals(requested.Members.Select(p => p.Identity)))
                { details.Add($"{requested.Name}: ข้าม — ตัวตน สมาชิก หรือกิจกรรมเปลี่ยน"); continue; }
                var appPids = actual.Group.Members.Select(p => p.Identity.Pid).ToHashSet();
                foreach (var target in actual.Group.Members)
                {
                    if (pending.ContainsKey(target.Identity.InstanceKey)) { details.Add($"{target.Name}: อยู่ใน Boost แล้ว ไม่ trim ซ้ำ"); continue; }
                    if (cancellationToken.IsCancellationRequested || BoostProcessActions.IsForeground(appPids)) { details.Add($"{target.Name}: ข้าม — กลับมาใช้งานหรือยกเลิก"); break; }
                    try
                    {
                        using var handle = BoostProcessActions.Open(target.Identity, trim);
                        uint original = BoostProcessActions.ReadPriority(handle);
                        if (original <= BoostProcessActions.LowPriority) { details.Add($"{target.Name}: priority ต่ำอยู่แล้ว ไม่เปลี่ยนค่า"); continue; }
                        var entry = new BoostEntry(target.Identity, target.Name, target.AppKey, settings.Profile, original, BoostProcessActions.LowPriority);
                        // Persist restore intent before touching the process; crash between either step is recoverable.
                        journal.SaveBoost(entry); pending.Add(entry.Identity.InstanceKey, entry);
                        if (BoostProcessActions.IsForeground(appPids)) { RestoreOne(entry); continue; }
                        BoostProcessActions.SetPriority(handle, entry.AppliedPriority); changed++;
                        bool didTrim = false;
                        if (trim && !BoostProcessActions.IsForeground(appPids)) { BoostProcessActions.Trim(handle); trimmed++; didTrim = true; }
                        details.Add($"{target.Name}: memory priority {original} → {entry.AppliedPriority}{(trim ? didTrim ? " · คืน working set หนึ่งครั้งแล้ว" : " · ข้าม trim เพราะแอปกลับมาใช้งาน" : "")}");
                    }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or StaleBoostTargetException)
                    { details.Add($"{target.Name}: ทำได้ไม่ครบ — {ex.Message}; ตรวจรายการคืนค่า"); }
                }
            }
            return new(changed == selected.Sum(g => g.Count) && (!trim || trimmed == changed) ? OperationStatus.Completed : OperationStatus.Partial,
                $"ลด memory priority {changed} process · trim สำเร็จ {trimmed} process · ไม่มีการพักหรือปิดแอป", details);
        }
    }
    public ActionResult RestoreAll()
    {
        lock (gate)
        {
            var details = pending.Values.ToArray().Select(RestoreOne).ToArray();
            return new(pending.Count == 0 ? OperationStatus.Completed : OperationStatus.Partial,
                pending.Count == 0 ? "จบโหมด Boost แล้ว คืนค่าที่ RamMx เปลี่ยนหรือยืนยันว่าเป้าหมายจบไปแล้ว" : $"ยังคืนค่าไม่ได้ {pending.Count} process กดคืนค่าเพื่อลองอีกครั้ง", details);
        }
    }
    private string RestoreOne(BoostEntry entry)
    {
        try
        {
            if (entry.OriginalPriority is < 3 or > 5 || entry.AppliedPriority != BoostProcessActions.LowPriority) throw new InvalidOperationException("ข้อมูล priority ไม่ถูกต้อง");
            using var handle = BoostProcessActions.Open(entry.Identity);
            var current = BoostProcessActions.ReadPriority(handle);
            if (current == entry.AppliedPriority) BoostProcessActions.SetPriority(handle, entry.OriginalPriority);
            journal.RemoveBoost(entry.Identity.InstanceKey); pending.Remove(entry.Identity.InstanceKey);
            return $"{entry.Name}: {(current == entry.AppliedPriority ? "คืน priority เดิมแล้ว" : "ค่าเปลี่ยนจากภายนอกแล้ว ไม่เขียนทับ")}";
        }
        catch (StaleBoostTargetException)
        {
            try
            {
                journal.RemoveBoost(entry.Identity.InstanceKey); pending.Remove(entry.Identity.InstanceKey);
                return $"{entry.Name}: process เดิมจบแล้ว ไม่แตะ PID ที่นำกลับมาใช้";
            }
            catch (Exception ex) { return $"{entry.Name}: process เดิมจบแล้ว แต่ยังบันทึกผลคืนค่าไม่ได้ — {ex.Message}"; }
        }
        catch (Exception ex) { return $"{entry.Name}: ยังคืนค่าไม่ได้ — {ex.Message}"; }
    }
}
