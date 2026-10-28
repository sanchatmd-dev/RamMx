namespace RamMx.Core;

public enum AppRole { UserApp, WindowsComponent, Security, ServiceHost, SharedRuntime, Unknown, Utility }
public enum SignatureState { NotChecked, Valid, Unsigned, Invalid, Unavailable }
public enum ProcessMemorySource { ProcessApi, PerformanceCounters }
public enum ActionKind { Close, ForceStop, Uninstall, Cleanup, DefenderScan }
public enum OperationStatus { Planned, Running, Completed, Partial, Failed, Cancelled, RebootRequired, UnknownOutcome }

public sealed record MemorySnapshot(DateTimeOffset Timestamp, ulong Usable, ulong Available, ulong Commit, ulong CommitLimit,
    ulong KernelPaged, ulong KernelNonpaged, ulong SystemCache, ulong Installed, ulong DiskTotal, ulong DiskFree)
{
    public double AvailablePercent => Usable == 0 ? 0 : 100d * Available / Usable;
    public double CommitPercent => CommitLimit == 0 ? 0 : 100d * Commit / CommitLimit;
    public double UsedPercent => 100 - AvailablePercent;
}

public sealed record ProcessIdentity(int Pid, long CreationFileTime, string Path, int SessionId)
{
    public string InstanceKey => $"{Pid}:{CreationFileTime}";
}

public sealed record ProcessSample(ProcessIdentity Identity, string Name, string AppKey, string DisplayName,
    AppRole Role, ulong WorkingSet, ulong? PrivateWorkingSet, ulong PrivateCommit, double CpuPercent,
    bool? Critical, bool? Protected, bool HasWindow, bool IsForeground, string? PackageFullName, string Description, bool MemoryAvailable = true,
    ProcessMemorySource MemorySource = ProcessMemorySource.ProcessApi, int? ParentPid = null, long? TotalCpuTicks = null, ulong? IoBytes = null)
{
    public bool CanStop => ActionPolicy.Evaluate(this).Allowed;
    public string MemorySourceLabel => !MemoryAvailable ? "อ่านไม่ได้" : MemorySource == ProcessMemorySource.PerformanceCounters ? "Windows Performance Counters" : "Windows Process API";
}

public sealed class ProcessGroup : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public required string AppKey { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<ProcessSample> Members { get; set; }
    public AppRole Role => Members[0].Role;
    public string RoleLabel => Labels.Role(Role);
    public string Description => Members[0].Description;
    public string Path => Members[0].Identity.Path;
    public int Count => Members.Count;
    public ulong PrivateBytes => (ulong)Members.Where(p => p.PrivateWorkingSet.HasValue).Sum(p => (decimal)p.PrivateWorkingSet!.Value);
    public ulong WorkingSet => (ulong)Members.Sum(p => (decimal)p.WorkingSet);
    public ulong Commit => (ulong)Members.Sum(p => (decimal)p.PrivateCommit);
    public int Coverage => Members.Count(p => p.PrivateWorkingSet.HasValue);
    public string RamLabel => Coverage == 0 ? "อ่านไม่ได้" : Format.Bytes(PrivateBytes) + (Coverage < Count ? " *" : "");
    public string CommitLabel => Members.All(p => !p.MemoryAvailable) ? "อ่านไม่ได้" : Format.Bytes(Commit) + (Members.Any(p => !p.MemoryAvailable) ? " *" : "");
    public double Cpu => Members.Sum(p => p.CpuPercent);
    public string CpuLabel => $"{Cpu:F1}%";
    public bool Foreground => Members.Any(p => p.IsForeground);
    public bool CanStop => Members.All(p => ActionPolicy.Evaluate(p).Allowed);
    public string StopReason => Members.Select(ActionPolicy.Evaluate).FirstOrDefault(p => !p.Allowed)?.Reason ?? "ปิดได้เมื่อคุณไม่มีงานค้าง";
    public string StatusLabel => Foreground ? "กำลังใช้งาน" : Members.Any(p => p.HasWindow) ? "มีหน้าต่าง" : "เบื้องหลัง";
    private bool keep;
    public bool Keep { get => keep; set { if (keep != value) { keep = value; PropertyChanged?.Invoke(this, new(nameof(KeepLabel))); } } }
    public string KeepLabel => Keep ? "เก็บไว้" : "";
    public void UpdateSnapshot(ProcessGroup incoming)
    {
        if (incoming.AppKey != AppKey) throw new ArgumentException("Cannot replace a group with another app");
        var before = VisibleValues();
        Members = incoming.Members; keep = incoming.Keep;
        foreach (var value in VisibleValues())
            if (!Equals(before[value.Key], value.Value)) PropertyChanged?.Invoke(this, new(value.Key));
    }
    private Dictionary<string, object> VisibleValues() => new()
    {
        [nameof(RamLabel)] = RamLabel, [nameof(CommitLabel)] = CommitLabel, [nameof(CpuLabel)] = CpuLabel,
        [nameof(Count)] = Count, [nameof(RoleLabel)] = RoleLabel, [nameof(StatusLabel)] = StatusLabel,
        [nameof(KeepLabel)] = KeepLabel, [nameof(CanStop)] = CanStop, [nameof(Path)] = Path,
        [nameof(Description)] = Description, [nameof(StopReason)] = StopReason
    };
    // WPF uses this as the row's accessible name. Keep it stable as metrics change;
    // RAM and CPU remain exposed through the accessible values of their own cells.
    public override string ToString() => Name;
}

public sealed record InstalledApp(string Key, string Name, string Version, string Publisher, string InstallLocation,
    string UninstallCommand, string Kind, string Scope, string? ProductCode, string? PackageFullName, long? EstimatedKiB,
    bool Protected, string RegistrationPath)
{
    public string SizeLabel => EstimatedKiB.HasValue ? Format.Bytes((ulong)Math.Max(0, EstimatedKiB.Value) * 1024) + " ≈" : "ไม่ทราบ";
    public bool CanUninstall => !Protected && (!string.IsNullOrWhiteSpace(UninstallCommand) || PackageFullName != null || ProductCode != null);
    public string ProtectionLabel => Protected ? "ส่วนประกอบ / ใช้ร่วมกัน" : "";
    public override string ToString() => $"{Name} · {Kind} · {Publisher}";
}

public sealed record Evidence(string Title, string Detail, string Severity, DateTimeOffset ObservedAt);
public sealed record Inspection(string Name, string Path, SignatureState Signature, string Publisher, IReadOnlyList<Evidence> Evidence)
{
    public string SignatureLabel => Signature switch { SignatureState.Valid => "ตรวจสอบลายเซ็นได้", SignatureState.Unsigned => "ไม่มีลายเซ็น", SignatureState.Invalid => "ลายเซ็นไม่ผ่าน", _ => "ยังตรวจสอบไม่ได้" };
}
public sealed record Recommendation(string AppKey, string Title, string Reason, string Benefit, string Action);
public sealed record ActionResult(OperationStatus Status, string Message, IReadOnlyList<string>? Details = null, int? NativeCode = null, BoostMemoryReport? BoostMemory = null);
public sealed record HistoryEntry(string Id, DateTimeOffset Timestamp, string Action, string Target, string Status, string Message);
public sealed record StartupEntry(string Name, string Source, string Command, string Scope);

public sealed class UserSettings
{
    public int SchemaVersion { get; set; } = 1;
    public int RefreshSeconds { get; set; } = 2;
    public string Profile { get; set; } = "ทั่วไป";
    public string Theme { get; set; } = "Dark";
    public bool MinimizeToTray { get; set; }
    public bool StartWithWindows { get; set; }
    public double LowAvailablePercent { get; set; } = 10;
    public double HighCommitPercent { get; set; } = 85;
    public HashSet<string> KeepApps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, HashSet<string>> ProfileCandidates { get; set; } = new();
    public Dictionary<string, HashSet<string>> BoostAllowedApps { get; set; } = new();
    public int BoostIdleMinutes { get; set; } = 15;
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 830;
    public void Validate()
    {
        if (SchemaVersion != 1) throw new InvalidDataException("Settings schema ไม่รองรับ");
        RefreshSeconds = Math.Clamp(RefreshSeconds, 1, 30);
        LowAvailablePercent = double.IsFinite(LowAvailablePercent) ? Math.Clamp(LowAvailablePercent, 1, 50) : 10;
        HighCommitPercent = double.IsFinite(HighCommitPercent) ? Math.Clamp(HighCommitPercent, 50, 99) : 85;
        WindowWidth = double.IsFinite(WindowWidth) ? Math.Clamp(WindowWidth, 960, 2400) : 1280;
        WindowHeight = double.IsFinite(WindowHeight) ? Math.Clamp(WindowHeight, 600, 1600) : 830;
        KeepApps = new(KeepApps ?? [], StringComparer.OrdinalIgnoreCase);
        ProfileCandidates ??= new();
        if (KeepApps.Count > 10000 || ProfileCandidates.Count > 30) throw new InvalidDataException("Settings ใหญ่เกินกำหนด");
        var profiles = new Dictionary<string, HashSet<string>>();
        foreach (var entry in ProfileCandidates)
        {
            if (entry.Value?.Count > 10000) throw new InvalidDataException("รายการแอปในโปรไฟล์ใหญ่เกินกำหนด");
            profiles[entry.Key] = new(entry.Value ?? [], StringComparer.OrdinalIgnoreCase);
        }
        ProfileCandidates = profiles;
        BoostIdleMinutes = Math.Clamp(BoostIdleMinutes, 5, 120);
        BoostAllowedApps ??= new();
        if (BoostAllowedApps.Count > 30 || BoostAllowedApps.Any(p => p.Value?.Count > 1000)) throw new InvalidDataException("รายการ Boost ใหญ่เกินกำหนด");
        BoostAllowedApps = BoostAllowedApps.ToDictionary(p => p.Key, p => new HashSet<string>(p.Value ?? [], StringComparer.OrdinalIgnoreCase));
        if (Profile is not ("ทั่วไป" or "ทำงาน" or "เล่นเกม")) Profile = "ทั่วไป";
        if (Theme is not ("Dark" or "Light")) Theme = "Dark";
    }
}

public static class Format
{
    public static string Bytes(ulong value) => value >= 1073741824 ? $"{value / 1073741824d:F2} GiB"
        : value >= 1048576 ? $"{value / 1048576d:F1} MiB"
        : value >= 1024 ? $"{value / 1024d:F1} KiB" : $"{value} B";
}
public static class Labels
{
    public static string Role(AppRole role) => role switch { AppRole.UserApp => "แอปผู้ใช้", AppRole.WindowsComponent => "Windows", AppRole.Security => "ความปลอดภัย", AppRole.ServiceHost => "บริการระบบ", AppRole.SharedRuntime => "Runtime ใช้ร่วมกัน", AppRole.Utility => "ตัวจัดการ RamMx", _ => "ยังระบุไม่ได้" };
}
