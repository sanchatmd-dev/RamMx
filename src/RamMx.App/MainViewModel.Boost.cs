using System.ComponentModel;
using System.Windows.Input;
using RamMx.Core;
using RamMx.Windows;

namespace RamMx.App;

public sealed class BoostRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private readonly Action<BoostRow, bool> changed;
    private bool allowed, keep;
    private string ram, idle;
    public BoostAssessment Assessment { get; private set; }
    public string AppKey => Assessment.Group.AppKey;
    public string Name => Assessment.Group.Name;
    public string Ram => ram;
    public string Idle => idle;
    public string Reason => Assessment.Reason;
    public bool CanAllow => Assessment.CanAllow;
    public bool Eligible => Assessment.Eligible;
    public bool Allowed { get => allowed; set { if (allowed == value) return; allowed = value; changed(this, false); } }
    public bool Keep { get => keep; set { if (keep == value) return; keep = value; changed(this, true); } }
    public BoostRow(BoostAssessment assessment, bool allowed, bool keep, Action<BoostRow, bool> changed)
    { Assessment = assessment; ram = assessment.Group.RamLabel; idle = $"{Math.Floor(assessment.Idle.TotalMinutes):0} นาที"; this.allowed = allowed; this.keep = keep; this.changed = changed; }
    public void Update(BoostAssessment assessment, bool permitted, bool protectedApp)
    {
        var before = (Name, Ram, Idle, Reason, CanAllow, Eligible, allowed, keep);
        Assessment = assessment; allowed = permitted; keep = protectedApp;
        ram = assessment.Group.RamLabel; idle = $"{Math.Floor(assessment.Idle.TotalMinutes):0} นาที";
        if (before.Name != Name) PropertyChanged?.Invoke(this, new(nameof(Name)));
        if (before.Ram != Ram) PropertyChanged?.Invoke(this, new(nameof(Ram)));
        if (before.Idle != Idle) PropertyChanged?.Invoke(this, new(nameof(Idle)));
        if (before.Reason != Reason) PropertyChanged?.Invoke(this, new(nameof(Reason)));
        if (before.CanAllow != CanAllow) PropertyChanged?.Invoke(this, new(nameof(CanAllow)));
        if (before.Eligible != Eligible) PropertyChanged?.Invoke(this, new(nameof(Eligible)));
        if (before.allowed != allowed) PropertyChanged?.Invoke(this, new(nameof(Allowed)));
        if (before.keep != keep) PropertyChanged?.Invoke(this, new(nameof(Keep)));
    }
    public override string ToString() => Name;
}

public sealed partial class MainViewModel
{
    private BoostService boost = null!;
    private bool boostTrim;
    private string boostResult = "ยังไม่ได้ Boost · การลด memory priority ให้ Windows เลือกคืน RAM เมื่อจำเป็น ไม่รับประกันยอด RAM ที่เพิ่ม";
    public BulkObservableCollection<BoostRow> BoostRows { get; } = [];
    public int[] BoostIdleOptions { get; } = [5, 15, 30, 60, 120];
    public int BoostIdleMinutes { get => Settings.BoostIdleMinutes; set { Settings.BoostIdleMinutes = value; SaveSettings(); Notify(); RefreshBoostRows(); } }
    // Explicit per-run choice: do not persist or silently repeat a working-set trim.
    public bool BoostTrim { get => boostTrim; set { boostTrim = value; Notify(); } }
    public string BoostResult { get => boostResult; private set { boostResult = value; Notify(); } }
    public string BoostSummary => $"พร้อม Boost {BoostRows.Count(r => r.Eligible)} แอป · รอคืนค่า {boost.PendingCount} process";
    public ICommand OpenBoostCommand { get; private set; } = null!;
    public ICommand BoostCommand { get; private set; } = null!;
    public ICommand EndBoostCommand { get; private set; } = null!;
    public ICommand RefreshBoostCommand { get; private set; } = null!;
    public ICommand BoostBrowserHelpCommand { get; private set; } = null!;

    private void InitializeBoost()
    {
        boost = new BoostService(store, () => new TelemetryService { Inventory = telemetry.Inventory }.ReadProcesses());
        OpenBoostCommand = new Command(() => { Page = 6; return Task.CompletedTask; });
        BoostCommand = new Command(() => RunAsync(StartBoostAsync), () => !Busy && BoostRows.Any(r => r.Eligible));
        EndBoostCommand = new Command(() => RunAsync(EndBoostAsync), () => !Busy && boost.PendingCount > 0);
        RefreshBoostCommand = new Command(() => RunAsync(() => SampleAsync(true)), () => !Busy);
        BoostBrowserHelpCommand = new Command(() =>
        {
            interaction.Show("พักแท็บด้วยเบราว์เซอร์", "Chrome: Settings → Performance → Memory Saver\nEdge: Settings → System and performance → Sleeping tabs\n\nเพิ่มเว็บ Codex/ChatGPT/Claude และเว็บงานที่ต้องทำต่อเนื่องไว้ในรายการ Always keep active\n\nRamMx ไม่พักทั้งเบราว์เซอร์และไม่เปลี่ยนการตั้งค่านี้อัตโนมัติ เพราะแต่ละแท็บอาจใช้โปรเซสร่วมกัน");
            return Task.CompletedTask;
        });
    }
    private UserSettings CopyBoostSettings() => new()
    {
        Profile = Settings.Profile, BoostIdleMinutes = Settings.BoostIdleMinutes,
        KeepApps = new(Settings.KeepApps, StringComparer.OrdinalIgnoreCase),
        BoostAllowedApps = Settings.BoostAllowedApps.ToDictionary(p => p.Key, p => new HashSet<string>(p.Value, StringComparer.OrdinalIgnoreCase))
    };
    private void RefreshBoostRows()
    {
        if (boost == null) return;
        string beforeSummary = boostSummary;
        var settings = CopyBoostSettings();
        var allowed = settings.BoostAllowedApps.GetValueOrDefault(settings.Profile) ?? [];
        var plan = boost.Review(allGroups.SelectMany(g => g.Members).ToArray(), settings, DateTimeOffset.Now)
            .Where(a => a.CanAllow || a.Group.Role == AppRole.UserApp || allowed.Contains(a.Group.AppKey)).ToList();
        // Saved choices remain removable even while their app is closed.
        foreach (var key in allowed.Where(key => plan.All(a => a.Group.AppKey != key)))
        {
            var placeholder = new ProcessGroup { AppKey = key, Name = key, Members = [] };
            plan.Add(new(placeholder, true, false, TimeSpan.Zero, "แอปไม่ได้ทำงาน / ไม่มีข้อมูลปัจจุบัน"));
        }
        var wanted = plan.Select(p => p.Group.AppKey).ToHashSet();
        for (int i = BoostRows.Count - 1; i >= 0; i--) if (!wanted.Contains(BoostRows[i].AppKey)) BoostRows.RemoveAt(i);
        var rows = BoostRows.ToDictionary(r => r.AppKey);
        foreach (var item in plan)
        {
            bool permitted = allowed.Contains(item.Group.AppKey), keep = settings.KeepApps.Contains(item.Group.AppKey);
            if (rows.TryGetValue(item.Group.AppKey, out var existing)) existing.Update(item, permitted, keep);
            else BoostRows.Add(new(item, permitted, keep, ChangeBoostChoice));
        }
        boostSummary = BoostSummary;
        if (beforeSummary != boostSummary) { Notify(nameof(BoostSummary)); CommandManager.InvalidateRequerySuggested(); }
    }
    private string boostSummary = "";
    private void ChangeBoostChoice(BoostRow row, bool keepChanged)
    {
        if (Busy) { RefreshBoostRows(); return; }
        if (keepChanged) { if (row.Keep) Settings.KeepApps.Add(row.AppKey); else Settings.KeepApps.Remove(row.AppKey); }
        else
        {
            if (!Settings.BoostAllowedApps.TryGetValue(Profile, out var allowed)) Settings.BoostAllowedApps[Profile] = allowed = new(StringComparer.OrdinalIgnoreCase);
            if (row.Allowed && row.CanAllow) allowed.Add(row.AppKey); else allowed.Remove(row.AppKey);
        }
        SaveSettings(); RefreshBoostRows();
    }
    private async Task StartBoostAsync()
    {
        await SampleAsync(true); RefreshBoostRows();
        var targets = BoostRows.Where(r => r.Eligible).Select(r => new ProcessGroup
        { AppKey = r.AppKey, Name = r.Name, Members = r.Assessment.Group.Members.ToArray() }).ToArray();
        if (targets.Length == 0) { BoostResult = "ยังไม่มีแอปที่ผ่านเงื่อนไข เลือกอนุญาตแล้วรอช่วงกิจกรรมต่ำ"; return; }
        bool trim = BoostTrim; var settings = CopyBoostSettings();
        if (!interaction.Confirm("Boost RAM — ตรวจรายการ", $"โปรไฟล์ {settings.Profile} · ไม่ใช้/กิจกรรมต่ำอย่างน้อย {settings.BoostIdleMinutes} นาที\n\n{string.Join("\n", targets.Select(g => $"• {g.Name} — {g.Count} process · {g.RamLabel}"))}\n\nลด memory priority ของรายการเหล่านี้ แอปยังทำงาน\n{(trim ? "เพิ่มการคืน working set ครั้งเดียว: เมื่อกลับมาใช้อาจหน่วง และ RAM อาจเพิ่มกลับ" : "ไม่ได้เลือก trim working set")}\n\nไม่ยืนยันว่าแอปไม่มีงานค้าง และไม่รับประกันจำนวน RAM ที่จะคืน\nกลับมาใช้แอป / เปลี่ยนโปรไฟล์ / จบโหมด / ปิด RamMx จะพยายามคืน priority เดิม")) return;
        var id = store.Begin("Boost RAM", string.Join(", ", targets.Select(g => g.Name)));
        var before = await Task.Run(telemetry.ReadMemory);
        try
        {
            var result = await ExecuteRecorded(id, () => Task.Run(() => boost.Start(targets, settings, trim, actionCancellation!.Token)));
            await Task.Delay(1000);
            var after = await Task.Run(telemetry.ReadMemory);
            double change = ((double)after.Available - before.Available) / 1048576;
            BoostResult = $"{result.Message}\nRAM พร้อมใช้ {Format.Bytes(before.Available)} → {Format.Bytes(after.Available)} ({change:+0.0;-0.0;0.0} MiB)\nเป็นการเปลี่ยนแปลงทั้งเครื่อง แอปอื่นมีผลร่วมด้วย\n{string.Join("\n", result.Details ?? [])}";
            Status = result.Message;
        }
        finally { BoostTrim = false; RefreshBoostRows(); }
    }
    private async Task EndBoostAsync()
    {
        var id = store.Begin("Restore Boost", "คืน memory priority ที่ RamMx เปลี่ยน");
        var result = await ExecuteRecorded(id, () => Task.Run(boost.RestoreAll));
        BoostResult = result.Message + "\n" + string.Join("\n", result.Details ?? []); RefreshBoostRows();
    }
    private async Task RecoverBoostAsync()
    {
        if (boost.PendingCount > 0) await EndBoostAsync();
    }
}
