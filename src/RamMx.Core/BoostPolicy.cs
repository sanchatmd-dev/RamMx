namespace RamMx.Core;

public sealed record BoostEntry(ProcessIdentity Identity, string Name, string AppKey, string Profile, uint OriginalPriority, uint AppliedPriority);
public interface IBoostJournal
{
    IReadOnlyList<BoostEntry> PendingBoost();
    void SaveBoost(BoostEntry entry);
    void RemoveBoost(string instanceKey);
}

public sealed record BoostAssessment(ProcessGroup Group, bool CanAllow, bool Eligible, TimeSpan Idle, string Reason);

public sealed class BoostActivityTracker
{
    private sealed record Activity(DateTimeOffset SampledAt, DateTimeOffset ActiveAt, long? Cpu, ulong? Io);
    private readonly Dictionary<string, Activity> history = new();
    public void Observe(DateTimeOffset now, IReadOnlyList<ProcessSample> samples)
    {
        var live = samples.Select(p => p.Identity.InstanceKey).ToHashSet();
        foreach (var stale in history.Keys.Where(k => !live.Contains(k)).ToArray()) history.Remove(stale);
        var foregroundApps = samples.Where(p => p.IsForeground).Select(p => p.AppKey).ToHashSet();
        foreach (var p in samples)
        {
            bool active = foregroundApps.Contains(p.AppKey) || !p.TotalCpuTicks.HasValue || !p.IoBytes.HasValue;
            DateTimeOffset activeAt = now;
            if (history.TryGetValue(p.Identity.InstanceKey, out var old))
            {
                var seconds = (now - old.SampledAt).TotalSeconds;
                // A gap/clock change cannot count as observed inactivity. Timers poll at most every 30 seconds.
                if (seconds >= 0 && seconds <= 90 && old.Cpu.HasValue && old.Io.HasValue && p.TotalCpuTicks >= old.Cpu && p.IoBytes >= old.Io)
                {
                    if (seconds > 0)
                        active |= (p.TotalCpuTicks - old.Cpu) / 10000000d / seconds > .02 ||
                            (p.IoBytes!.Value - old.Io.Value) / seconds > 64 * 1024;
                    else active |= p.TotalCpuTicks != old.Cpu || p.IoBytes != old.Io;
                    activeAt = active ? now : old.ActiveAt;
                }
            }
            history[p.Identity.InstanceKey] = new(now, activeAt, p.TotalCpuTicks, p.IoBytes);
        }
    }
    public TimeSpan IdleFor(ProcessSample sample, DateTimeOffset now) => history.TryGetValue(sample.Identity.InstanceKey, out var value)
        && now >= value.SampledAt && now - value.SampledAt <= TimeSpan.FromSeconds(90) ? now - value.ActiveAt : TimeSpan.Zero;
}

public static class BoostPolicy
{
    private static readonly HashSet<string> WorkNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "code", "code-insiders", "devenv", "rider64", "idea64", "pycharm64", "windowsterminal", "wt", "cmd", "powershell", "pwsh",
        "node", "python", "pythonw", "java", "javaw", "dotnet", "msbuild", "testhost", "vstest.console", "git", "bash", "wsl", "wslhost",
        "docker", "com.docker.backend", "vmmem", "vmmemwsl", "msedgewebview2",
        // A browser can host the current AI session; tab ownership cannot be inferred from process IDs.
        "chrome", "msedge", "firefox", "brave", "opera"
    };
    public static bool IsWorkload(ProcessSample p) => WorkNames.Contains(p.Name) ||
        new[] { "codex", "claude", "chatgpt", "anthropic", "openai" }.Any(n => (p.Name + " " + p.Identity.Path).Contains(n, StringComparison.OrdinalIgnoreCase));

    public static HashSet<int> ProtectedWork(IReadOnlyList<ProcessSample> samples)
    {
        var byPid = samples.ToDictionary(p => p.Identity.Pid);
        var protectedPids = samples.Where(IsWorkload).Select(p => p.Identity.Pid).ToHashSet();
        // Protect owning app ancestors, stopping at Windows launchers rather than protecting the whole desktop.
        foreach (var seed in samples.Where(IsWorkload))
        {
            var child = seed; var seen = new HashSet<int>();
            for (int depth = 0; depth < 64 && child.ParentPid is int parentPid && seen.Add(parentPid); depth++)
            {
                if (!byPid.TryGetValue(parentPid, out var parent) || !ParentMatches(parent, child) || parent.Role == AppRole.WindowsComponent) break;
                protectedPids.Add(parentPid); child = parent;
            }
        }
        for (int pass = 0; pass < 64; pass++)
        {
            bool added = false;
            foreach (var p in samples)
                if (p.ParentPid is int parent && protectedPids.Contains(parent) && byPid.TryGetValue(parent, out var owner) && ParentMatches(owner, p))
                    added |= protectedPids.Add(p.Identity.Pid);
            if (!added) break;
        }
        return protectedPids;
    }

    private static bool ParentMatches(ProcessSample parent, ProcessSample child) => parent.Identity.CreationFileTime > 0 &&
        child.Identity.CreationFileTime > 0 && parent.Identity.CreationFileTime <= child.Identity.CreationFileTime;

    private static bool KnownAncestry(ProcessSample child, IReadOnlyDictionary<int, ProcessSample> byPid)
    {
        var seen = new HashSet<int>();
        for (int depth = 0; depth < 64; depth++)
        {
            if (child.ParentPid is not int parentPid || !seen.Add(parentPid) || !byPid.TryGetValue(parentPid, out var parent) || !ParentMatches(parent, child)) return false;
            if (parent.Role == AppRole.WindowsComponent && parent.Identity.Path.Length > 0) return true;
            child = parent;
        }
        return false;
    }

    public static IReadOnlyList<BoostAssessment> Assess(IReadOnlyList<ProcessSample> samples, UserSettings settings, BoostActivityTracker activity, DateTimeOffset now)
    {
        var byPid = samples.ToDictionary(p => p.Identity.Pid);
        var protectedPids = ProtectedWork(samples);
        var allowed = settings.BoostAllowedApps.GetValueOrDefault(settings.Profile) ?? [];
        var result = new List<BoostAssessment>();
        foreach (var group in samples.GroupBy(p => p.AppKey).Select(g => new ProcessGroup { AppKey = g.Key, Name = g.First().DisplayName, Members = g.ToArray() }))
        {
            string? blocked = group.Members.Any(p => protectedPids.Contains(p.Identity.Pid)) ? "ปกป้อง Codex/Claude เครื่องมือพัฒนา และเบราว์เซอร์" :
                !group.CanStop ? "ระบบ / runtime / ยืนยันตัวตนหรือสิทธิ์ไม่ได้" :
                group.Members.Any(p => !KnownAncestry(p, byPid)) ? "ยืนยันความเกี่ยวข้องกับงานปัจจุบันไม่ได้" : null;
            var idle = group.Members.Min(p => activity.IdleFor(p, now));
            string reason = blocked ?? (settings.KeepApps.Contains(group.AppKey) ? "อยู่ในรายการห้ามแตะ (Keep)" :
                group.Foreground ? "กำลังใช้งานอยู่" :
                !allowed.Contains(group.AppKey) ? "ยังไม่ได้อนุญาตในโปรไฟล์นี้" :
                idle < TimeSpan.FromMinutes(settings.BoostIdleMinutes) ? $"รอกิจกรรมต่ำต่อเนื่อง {settings.BoostIdleMinutes} นาที" : "พร้อมลด memory priority");
            bool eligible = blocked == null && !settings.KeepApps.Contains(group.AppKey) && !group.Foreground && allowed.Contains(group.AppKey) && idle >= TimeSpan.FromMinutes(settings.BoostIdleMinutes);
            result.Add(new(group, blocked == null, eligible, idle, reason));
        }
        return result.OrderByDescending(r => r.Eligible).ThenByDescending(r => r.CanAllow).ThenByDescending(r => r.Group.PrivateBytes).ToArray();
    }
}
