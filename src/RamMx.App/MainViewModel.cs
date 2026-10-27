using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using RamMx.Core;
using RamMx.Persistence;
using RamMx.Windows;

namespace RamMx.App;

public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> source)
    {
        var values = source.ToArray(); if (this.SequenceEqual(values)) return;
        Items.Clear(); foreach (var value in values) Items.Add(value);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

public sealed class Command(Func<Task> action, Func<bool>? enabled = null) : ICommand
{
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public bool CanExecute(object? parameter) => enabled?.Invoke() ?? true;
    public async void Execute(object? parameter) { if (CanExecute(parameter)) await action(); }
}

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private readonly StateStore store;
    private readonly IInteraction interaction;
    private readonly TelemetryService telemetry = new();
    private readonly InventoryService inventory = new();
    private readonly CacheService cache = new();
    private readonly RecommendationEngine recommender = new();
    private readonly BehaviourTracker behaviour = new();
    private readonly Queue<(DateTimeOffset Time, double Value)> chart = new();
    private readonly SemaphoreSlim sampleGate = new(1, 1);
    private CancellationTokenSource? actionCancellation;
    private IReadOnlyList<ProcessGroup> allGroups = [];
    private IReadOnlyList<InstalledApp> allInstalled = [];
    private MemorySnapshot? snapshot;
    private string search = "", appSearch = "", status = "กำลังเริ่มต้น…", details = "เลือกแอปเพื่อดูหน้าที่ ข้อมูลตัวตน และสิ่งที่อาจกระทบหากปิด", inspection = "เลือกแอปจากหน้าแอปที่ทำงาน แล้วกดตรวจตัวตน";
    private int page;
    private bool busy;
    private ProcessGroup? selectedGroup;
    private InstalledApp? selectedInstalled;
    private CacheRecipe? selectedRecipe;
    private DateTimeOffset lastPersist;
    private DateTimeOffset lastReview;
    private BackgroundCandidate? selectedReview;
    public BackgroundCandidate? SelectedReview { get => selectedReview; set { selectedReview = value; Notify(); CommandManager.InvalidateRequerySuggested(); } }
    public UserSettings Settings { get; }
    public bool Busy { get => busy; private set { busy = value; Notify(); CommandManager.InvalidateRequerySuggested(); } }
    public int Page { get => page; set { page = value; Notify(); } }
    private int reviewTab;
    public int ReviewTab { get => reviewTab; set { reviewTab = value; Notify(); } }
    public string Status { get => status; set { status = value; Notify(); } }
    public string Details { get => details; private set { details = value; Notify(); } }
    public string Inspection { get => inspection; private set { inspection = value; Notify(); } }
    public string Search { get => search; set { search = value; ApplyFilter(); Notify(); } }
    public string AppSearch { get => appSearch; set { appSearch = value; FilterInstalled(); Notify(); } }
    public string Available => snapshot == null ? "—" : Format.Bytes(snapshot.Available);
    public string Usable => snapshot == null ? "กำลังอ่าน…" : $"ใช้ได้ {Format.Bytes(snapshot.Usable)} / ติดตั้ง {Format.Bytes(snapshot.Installed)}";
    public string Commit => snapshot == null ? "—" : Format.Bytes(snapshot.Commit);
    public string CommitLimit => snapshot == null ? "" : $"จาก limit {Format.Bytes(snapshot.CommitLimit)} ({snapshot.CommitPercent:F0}%)";
    public string DiskFree => snapshot == null ? "—" : Format.Bytes(snapshot.DiskFree);
    public string DiskDetail => snapshot == null ? "" : $"เหลือ {100d * snapshot.DiskFree / Math.Max(1UL, snapshot.DiskTotal):F1}% ของไดรฟ์ระบบ";
    public double UsedPercent => snapshot?.UsedPercent ?? 0;
    public string MemoryLabel => snapshot == null ? "" : $"พร้อมใช้ {snapshot.AvailablePercent:F1}% · ส่วนที่ยังไม่พร้อมใช้ {snapshot.UsedPercent:F1}%";
    public string ProcessSummary => $"{allGroups.Sum(g => g.Count)} processes · {allGroups.Count} กลุ่ม · RAM เฉพาะแอปที่อ่านได้ ไม่รวมส่วนแชร์";
    public double[] Chart { get; private set; } = [];
    public string ChartCaption => $"Available RAM (%) · {(chart.Count < 2 ? 0 : (int)(chart.Last().Time - chart.Peek().Time).TotalSeconds)} วินาทีล่าสุด · ยิ่งสูงยิ่งมี RAM พร้อมใช้";
    public string Profile { get => Settings.Profile; set { Settings.Profile = value; SaveSettings(); Notify(); UpdateRecommendations(); } }
    public string Theme { get => Settings.Theme; set { Settings.Theme = value; App.SetTheme(value); SaveSettings(); Notify(); } }
    public int RefreshSeconds { get => Settings.RefreshSeconds; set { Settings.RefreshSeconds = value; SaveSettings(); Notify(); } }
    public bool MinimizeToTray { get => Settings.MinimizeToTray; set { Settings.MinimizeToTray = value; SaveSettings(); Notify(); } }
    public bool StartWithWindows
    {
        get => Settings.StartWithWindows;
        set
        {
            try { WindowsLinks.SetStartup(value, Environment.ProcessPath!); Settings.StartWithWindows = value; SaveSettings(); }
            catch (Exception ex) { Status = ex.Message; }
            Notify();
        }
    }
    public double LowAvailablePercent { get => Settings.LowAvailablePercent; set { Settings.LowAvailablePercent = value; SaveSettings(); Notify(); } }
    public double HighCommitPercent { get => Settings.HighCommitPercent; set { Settings.HighCommitPercent = value; SaveSettings(); Notify(); } }
    public ProcessGroup? SelectedGroup
    {
        get => selectedGroup;
        set
        {
            if (!ReferenceEquals(selectedGroup, value)) { selectedGroup = value; Notify(); }
            CommandManager.InvalidateRequerySuggested();
            if (value != null) Details = $"{value.Name} · {value.RoleLabel} · {value.Count} process\n{value.Description}\n{value.Path}\nRAM เฉพาะแอป {value.RamLabel} (อ่านได้ {value.Coverage}/{value.Count}) · Private commit {value.CommitLabel}\nแหล่งค่า RAM: {string.Join(", ", value.Members.Select(m => m.MemorySourceLabel).Distinct())}\n{value.StopReason}\nPID: {string.Join(", ", value.Members.Select(m => m.Identity.Pid))}";
        }
    }
    public InstalledApp? SelectedInstalled { get => selectedInstalled; set { selectedInstalled = value; Notify(); CommandManager.InvalidateRequerySuggested(); } }
    public CacheRecipe? SelectedRecipe { get => selectedRecipe; set { selectedRecipe = value; CacheFiles.Clear(); CacheSummary = "เลือกสแกนเพื่อดูไฟล์ก่อนลบ"; Notify(); } }
    private string cacheSummary = "เลือกแอปและสแกน cache";
    public string CacheSummary { get => cacheSummary; private set { cacheSummary = value; Notify(); } }
    public BulkObservableCollection<ProcessGroup> Groups { get; } = [];
    public BulkObservableCollection<InstalledApp> Installed { get; } = [];
    public BulkObservableCollection<Recommendation> Recommendations { get; } = [];
    public BulkObservableCollection<Evidence> Evidence { get; } = [];
    public BulkObservableCollection<BackgroundCandidate> BackgroundCandidates { get; } = [];
    public BulkObservableCollection<StartupEntry> Startup { get; } = [];
    public BulkObservableCollection<HistoryEntry> History { get; } = [];
    public BulkObservableCollection<CacheFile> CacheFiles { get; } = [];
    public IReadOnlyList<CacheRecipe> Recipes => cache.Recipes;
    public string[] Profiles { get; } = ["ทั่วไป", "ทำงาน", "เล่นเกม"];
    public string[] Themes { get; } = ["Dark", "Light"];
    public int[] Intervals { get; } = [1, 2, 5, 10];

    public ICommand RefreshCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand ForceCommand { get; }
    public ICommand AdminForceCommand { get; }
    public ICommand KeepCommand { get; }
    public ICommand CandidateCommand { get; }
    public ICommand InspectCommand { get; }
    public ICommand InspectReviewCommand { get; }
    public ICommand ShowFileCommand { get; }
    public ICommand UninstallCommand { get; }
    public ICommand ScanCacheCommand { get; }
    public ICommand CleanCacheCommand { get; }
    public ICommand DefenderCommand { get; }
    public ICommand WindowsSecurityCommand { get; }
    public ICommand StartupCommand { get; }
    public ICommand InstalledSettingsCommand { get; }
    public ICommand StorageCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ClearHistoryCommand { get; }

    public MainViewModel(IInteraction interaction, string? stateDirectory = null)
    {
        this.interaction = interaction; store = new(stateDirectory); Settings = store.Load(); App.SetTheme(Settings.Theme);
        bool CanAct() => !Busy && SelectedGroup is { CanStop: true };
        RefreshCommand = new Command(() => RunAsync(LoadInventoryAsync), () => !Busy);
        CloseCommand = new Command(() => StopAsync(false, false), CanAct);
        ForceCommand = new Command(() => StopAsync(true, false), CanAct);
        AdminForceCommand = new Command(() => StopAsync(true, true), CanAct);
        KeepCommand = new Command(() => { if (SelectedGroup is { } g) { if (!Settings.KeepApps.Remove(g.AppKey)) Settings.KeepApps.Add(g.AppKey); SaveSettings(); ApplyFilter(); UpdateRecommendations(); } return Task.CompletedTask; }, () => !Busy && SelectedGroup != null);
        CandidateCommand = new Command(() => { if (SelectedGroup is { } g) { if (!Settings.ProfileCandidates.TryGetValue(Profile, out var list)) Settings.ProfileCandidates[Profile] = list = []; if (!list.Remove(g.AppKey)) list.Add(g.AppKey); SaveSettings(); UpdateRecommendations(); Status = "ปรับรายการไม่ใช้ในโปรไฟล์แล้ว — ไม่มีการปิดอัตโนมัติ"; } return Task.CompletedTask; }, () => !Busy && SelectedGroup != null);
        InspectCommand = new Command(() => RunAsync(InspectAsync), () => !Busy && SelectedGroup != null);
        InspectReviewCommand = new Command(() => RunAsync(async () =>
        {
            var group = allGroups.FirstOrDefault(g => g.AppKey == SelectedReview?.AppKey);
            if (group == null) { Status = "แอปจบไปแล้ว กรุณารอรายการใหม่"; return; }
            SelectedGroup = group; await InspectAsync();
        }), () => !Busy && SelectedReview != null);
        ShowFileCommand = new Command(() => RunAsync(() => { WindowsLinks.ShowFile(SelectedGroup!.Path); return Task.CompletedTask; }), () => !Busy && SelectedGroup?.Path.Length > 0);
        UninstallCommand = new Command(() => RunAsync(UninstallAsync), () => !Busy && SelectedInstalled is { CanUninstall: true });
        ScanCacheCommand = new Command(() => RunAsync(ScanCacheAsync), () => !Busy && SelectedRecipe != null);
        CleanCacheCommand = new Command(() => RunAsync(CleanCacheAsync), () => !Busy && CacheFiles.Any(f => f.Selected));
        DefenderCommand = new Command(() => RunAsync(DefenderAsync), () => !Busy && SelectedGroup?.Path.Length > 0);
        WindowsSecurityCommand = new Command(() => RunAsync(() => { WindowsLinks.Security(); return Task.CompletedTask; }));
        StartupCommand = new Command(() => RunAsync(() => { WindowsLinks.StartupApps(); return Task.CompletedTask; }));
        InstalledSettingsCommand = new Command(() => RunAsync(() => { WindowsLinks.InstalledApps(); return Task.CompletedTask; }));
        StorageCommand = new Command(() => RunAsync(() => { WindowsLinks.Storage(); return Task.CompletedTask; }));
        ExportCommand = new Command(() => RunAsync(ExportAsync), () => !Busy);
        ImportCommand = new Command(() => RunAsync(ImportAsync), () => !Busy);
        CancelCommand = new Command(() => { actionCancellation?.Cancel(); return Task.CompletedTask; }, () => Busy);
        ClearHistoryCommand = new Command(() => RunAsync(() => { if (interaction.Confirm("ล้างประวัติ", "ล้างประวัติที่จบแล้วและกราฟย้อนหลังในเครื่อง ไม่ลบรายการที่ยังไม่ทราบผล")) { store.ClearHistory(); RefreshHistory(); } return Task.CompletedTask; }), () => !Busy);
        SelectedRecipe = Recipes[0]; RefreshHistory();
    }

    public async Task InitializeAsync() => await RunAsync(LoadInventoryAsync);
    private async Task LoadInventoryAsync()
    {
        Status = "กำลังอ่านรายการติดตั้งและ startup…";
        allInstalled = await Task.Run(inventory.Read); telemetry.Inventory = allInstalled; FilterInstalled();
        Replace(Startup, await Task.Run(inventory.ReadStartup));
        await SampleAsync(true);
    }
    public async Task SampleAsync(bool force = false, bool updateVisuals = true)
    {
        if (Busy && !force) return;
        await sampleGate.WaitAsync();
        try
        {
            var data = await Task.Run(() => (telemetry.ReadMemory(), telemetry.ReadProcesses()));
            if (Busy && !force) return; // A command may have opened its confirmation while this sample was in flight.
            snapshot = data.Item1;
            allGroups = TelemetryService.Group(data.Item2, Settings); behaviour.Observe(snapshot.Timestamp, allGroups);
            chart.Enqueue((snapshot.Timestamp, snapshot.AvailablePercent));
            while (chart.Count > 61 || (chart.Count > 1 && snapshot.Timestamp - chart.Peek().Time > TimeSpan.FromSeconds(60))) chart.Dequeue();
            if (snapshot.Timestamp - lastPersist > TimeSpan.FromMinutes(1)) { store.Record(snapshot); lastPersist = snapshot.Timestamp; }
            if (!updateVisuals) { recommender.Evaluate(snapshot, allGroups, Settings); return; }
            ApplyFilter();
            if (snapshot.Timestamp - lastReview >= TimeSpan.FromSeconds(10))
            {
                var key = SelectedReview?.AppKey;
                Replace(BackgroundCandidates, BackgroundReview.Evaluate(allGroups, behaviour, snapshot.Timestamp));
                SelectedReview = BackgroundCandidates.FirstOrDefault(c => c.AppKey == key); lastReview = snapshot.Timestamp;
            }
            Chart = chart.Select(point => point.Value).ToArray();
            foreach (var property in new[] { nameof(Available), nameof(Usable), nameof(Commit), nameof(CommitLimit), nameof(DiskFree), nameof(DiskDetail), nameof(UsedPercent), nameof(MemoryLabel), nameof(ProcessSummary), nameof(Chart), nameof(ChartCaption) }) Notify(property);
            UpdateRecommendations(); Status = $"อัปเดต {snapshot.Timestamp:HH:mm:ss} · ทำงานในเครื่อง · ไม่มีการส่ง process list ออกไป";
        }
        catch (Exception ex) { Status = "อ่านข้อมูลไม่สำเร็จ: " + ex.Message; }
        finally { sampleGate.Release(); }
    }
    private void ApplyFilter()
    {
        var selected = SelectedGroup?.AppKey;
        foreach (var group in allGroups) group.Keep = Settings.KeepApps.Contains(group.AppKey);
        var wanted = allGroups.Where(g => string.IsNullOrWhiteSpace(Search) || g.Name.Contains(Search, StringComparison.CurrentCultureIgnoreCase) || g.Path.Contains(Search, StringComparison.OrdinalIgnoreCase) || g.RoleLabel.Contains(Search, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        var keys = wanted.Select(g => g.AppKey).ToHashSet();
        for (int i = Groups.Count - 1; i >= 0; i--) if (!keys.Contains(Groups[i].AppKey)) Groups.RemoveAt(i);
        var existing = Groups.ToDictionary(g => g.AppKey);
        for (int i = 0; i < wanted.Length; i++)
        {
            if (existing.TryGetValue(wanted[i].AppKey, out var row))
            {
                row.UpdateSnapshot(wanted[i]);
                var index = Groups.IndexOf(row); if (index != i) Groups.Move(index, i);
            }
            else Groups.Insert(i, wanted[i]);
        }
        SelectedGroup = Groups.FirstOrDefault(g => g.AppKey == selected);
    }
    private void FilterInstalled() { var key = SelectedInstalled?.Key; Replace(Installed, allInstalled.Where(a => string.IsNullOrWhiteSpace(AppSearch) || (a.Name + " " + a.Publisher).Contains(AppSearch, StringComparison.CurrentCultureIgnoreCase))); SelectedInstalled = Installed.FirstOrDefault(a => a.Key == key); }
    private void UpdateRecommendations() { if (snapshot != null) Replace(Recommendations, recommender.Evaluate(snapshot, allGroups, Settings)); }
    private async Task StopAsync(bool force, bool admin)
    {
        await RunAsync(async () =>
        {
            var selected = SelectedGroup!;
            var group = new ProcessGroup { AppKey = selected.AppKey, Name = selected.Name, Members = selected.Members.ToArray(), Keep = selected.Keep };
            if (Settings.KeepApps.Contains(group.AppKey) && !interaction.Confirm("รายการที่คุณเลือกเก็บไว้", $"{group.Name} อยู่ใน Keep list ต้องการดำเนินการครั้งนี้หรือไม่?")) return;
            var title = force ? "ยืนยัน Force stop" : "ขอปิดแอปตามปกติ";
            if (!interaction.Confirm(title, $"{group.Name} · {group.Count} process\n{group.Path}\n\n{(force ? "งานที่ยังไม่บันทึกอาจสูญหาย การหยุดย้อนกลับไม่ได้" : "แอปอาจถามให้บันทึกงาน บาง process ไม่มีหน้าต่างและจะยังทำงานต่อ") }\n\n{string.Join("\n", group.Members.Select(m => $"{m.Name} · PID {m.Identity.Pid}"))}")) return;
            var before = telemetry.ReadMemory(); var id = store.Begin(title, group.Name);
            var result = await ExecuteRecorded(id, () => admin ? ElevationProtocol.RequestAsync("ForceStop", group.Members, Environment.ProcessPath!, actionCancellation!.Token)
                : new ProcessActions().ExecuteAsync(group.Members, force, actionCancellation!.Token));
            var after = telemetry.ReadMemory();
            interaction.Show("ผลการปิดแอป", result.Message + $"\n\nAvailable RAM ก่อน: {Format.Bytes(before.Available)}\nหลัง: {Format.Bytes(after.Available)}\nเป็นผลต่างที่สังเกตได้ มีงานอื่นเปลี่ยนพร้อมกันได้\n\n" + string.Join("\n", result.Details ?? []));
            await SampleAsync(true);
        });
    }
    private async Task InspectAsync()
    {
        var group = SelectedGroup!; Page = 4; ReviewTab = 1; Status = "กำลังตรวจ signature แบบ offline…";
        var result = await Task.Run(() => new SignatureService().Inspect(group.Name, group.Path));
        Inspection = $"{result.Name}\n{result.Path}\n{result.SignatureLabel} · {result.Publisher}";
        var evidence = result.Evidence.Concat(behaviour.EvidenceFor(group.AppKey, DateTimeOffset.Now)).ToList();
        foreach (var entry in Startup.Where(s => s.Command.Contains(group.Path, StringComparison.OrdinalIgnoreCase) && group.Path.Length > 0))
            evidence.Add(new("พบ startup ที่อ้าง executable นี้", $"{entry.Name} · {entry.Source}; เป็นหลักฐานการเปิดอัตโนมัติ ไม่ใช่ malware verdict", "ข้อมูลการเริ่มทำงาน", DateTimeOffset.Now));
        Replace(Evidence, evidence); Status = "ตรวจตัวตนแล้ว — ผลนี้แยกจากการสแกน malware";
    }
    private async Task DefenderAsync()
    {
        var target = SelectedGroup!.Members.First(p => p.Identity.Path.Length > 0);
        if (!interaction.Confirm("สแกนไฟล์ด้วย Defender", $"{target.Identity.Path}\n\nใช้สิทธิ์ผู้ดูแล Defender อาจกักกันไฟล์ตาม policy ปัจจุบัน ไม่มีการเปลี่ยน exclusions หรือปิดการป้องกัน")) return;
        var id = store.Begin("Defender scan", target.DisplayName);
        var result = await ExecuteRecorded(id, () => ElevationProtocol.RequestAsync("DefenderScan", [target], Environment.ProcessPath!, actionCancellation!.Token));
        interaction.Show("ผล Defender", result.Message + "\n" + string.Join("\n", result.Details ?? []));
    }
    private async Task UninstallAsync()
    {
        var app = SelectedInstalled!;
        if (!interaction.Confirm("ถอนติดตั้ง " + app.Name, $"{app.Publisher} · {app.Version} · {app.Kind} · {app.Scope}\n\nจะเปิดตัวถอนติดตั้งของแอปหรือ Windows package manager\nตัวถอนติดตั้งอาจลบ settings และข้อมูลส่วนตัวด้วย โดยเฉพาะ MSIX โปรดสำรองข้อมูลผ่านแอปก่อนดำเนินการ\n\nRamMx ไม่รับรองการเก็บข้อมูลที่ตัวถอนลบ และไม่ถอน shared components ที่ปกป้องไว้\nหลังถอนแล้วสามารถเลือกสแกน cache ที่รองรับ ไม่มีการลบ leftovers อัตโนมัติ")) return;
        var id = store.Begin("Uninstall", app.Name); Status = "รอตัวถอนติดตั้ง… อาจมีหน้าต่างจากผู้ผลิต";
        var result = await ExecuteRecorded(id, () => new UninstallService().UninstallAsync(app, actionCancellation!.Token));
        interaction.Show("ผลถอนติดตั้ง", result.Message); await LoadInventoryAsync();
        if (result.Status == OperationStatus.Completed)
        {
            var recipe = Recipes.FirstOrDefault(r => app.Name.Contains(r.Id, StringComparison.OrdinalIgnoreCase) || (r.Id == "vscode" && app.Name.Contains("Visual Studio Code", StringComparison.OrdinalIgnoreCase)));
            if (recipe != null) { SelectedRecipe = recipe; Status = "เลือก recipe แล้ว กดสแกน cache เพื่อตรวจ leftovers ก่อนลบ"; }
        }
    }
    private async Task ScanCacheAsync()
    {
        var recipe = SelectedRecipe!; Status = "กำลังสแกนเฉพาะ cache roots ที่กำหนด…";
        var result = await Task.Run(() => cache.Scan(recipe, actionCancellation!.Token)); Replace(CacheFiles, result.Files);
        CacheSummary = $"{result.Files.Count:N0} ไฟล์ · {Format.Bytes(result.Bytes)} · บันทึกที่ข้าม {result.Skipped.Count} รายการ{(result.Truncated ? " · ถึงขีดจำกัดการสแกน แสดงผลบางส่วน" : "")}\n{(cache.IsRunning(recipe) ? "แอปยังเปิดอยู่ ปิดแอปก่อนล้าง" : "เลือกไฟล์และตรวจ path ก่อนลบถาวร")}\nไม่รวม cookies, login, bookmarks, settings และเอกสาร";
        Status = "สแกนเสร็จ — ยังไม่มีการลบไฟล์";
    }
    private async Task CleanCacheAsync()
    {
        var recipe = SelectedRecipe!; var files = CacheFiles.Where(f => f.Selected).ToArray();
        if (cache.IsRunning(recipe)) { interaction.Show("แอปยังทำงานอยู่", "โปรดปิดแอปด้วยตนเองก่อนล้าง cache เพื่อไม่ให้ไฟล์ถูกสร้างใหม่ระหว่างทำ"); return; }
        if (!interaction.Confirm("ลบ cache ที่เลือกถาวร", $"{recipe.Name}\n{files.Length:N0} ไฟล์ · {Format.Bytes((ulong)files.Sum(f => f.Size))}\n\nไฟล์จะถูกลบถาวร ไม่ผ่าน Recycle Bin และ undo ไม่ได้ แอปจะสร้าง cache ใหม่เมื่อจำเป็น\nไฟล์ที่เปลี่ยน ถูกล็อก หรือเป็นลิงก์จะถูกข้าม\n\nRoots:\n{string.Join("\n", files.Select(f => f.Root).Distinct())}")) return;
        var id = store.Begin("Clean cache", recipe.Name); var before = telemetry.ReadMemory();
        var result = await ExecuteRecorded(id, () => Task.Run(() => cache.Delete(recipe, files, actionCancellation!.Token)));
        var after = telemetry.ReadMemory();
        interaction.Show("ผลล้าง cache", result.Message + $"\n\nพื้นที่ว่างก่อน {Format.Bytes(before.DiskFree)}\nหลัง {Format.Bytes(after.DiskFree)}\nมีการใช้ดิสก์จากแอปอื่นพร้อมกันได้\n\n" + string.Join("\n", result.Details?.Take(30) ?? []));
        await ScanCacheAsync();
    }
    private async Task<ActionResult> ExecuteRecorded(string id, Func<Task<ActionResult>> action)
    {
        ActionResult result;
        try { result = await action(); }
        catch (OperationCanceledException) { result = new(OperationStatus.UnknownOutcome, "ยกเลิกการรอผล ตรวจเป้าหมายก่อนทำซ้ำ"); }
        catch (Exception ex) { result = new(OperationStatus.Failed, ex.Message); }
        store.Complete(id, result); RefreshHistory(); return result;
    }
    private Task ExportAsync()
    {
        if (!interaction.Confirm("Export settings", "ไฟล์อาจมี path ของแอปและรายการความชอบส่วนตัว ตรวจไฟล์ก่อนแชร์ให้ผู้อื่น")) return Task.CompletedTask;
        var path = interaction.SaveFile("RamMx-settings.json"); if (path != null) File.WriteAllText(path, JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true })); return Task.CompletedTask;
    }
    private Task ImportAsync()
    {
        var path = interaction.OpenFile(); if (path == null) return Task.CompletedTask;
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Settings file exceeds 1 MiB");
        var incoming = StateStore.Import(File.ReadAllText(path));
        if (!interaction.Confirm("Import settings", $"โปรไฟล์ {incoming.Profile} · Keep list {incoming.KeepApps.Count} รายการ · Refresh {incoming.RefreshSeconds}s\nจะเขียนทับ preferences ปัจจุบัน แต่ไม่เปิด startup หรือดำเนินการกับแอปใด")) return Task.CompletedTask;
        incoming.StartWithWindows = Settings.StartWithWindows;
        store.Save(incoming); interaction.Show("บันทึกแล้ว", "ปิดและเปิด RamMx ใหม่เพื่อใช้ settings ที่นำเข้า"); imported = true; return Task.CompletedTask;
    }
    private bool imported;
    public void SaveSettings() { if (!imported) { try { store.Save(Settings); } catch (Exception ex) { Status = "บันทึก settings ไม่สำเร็จ: " + ex.Message; } } }
    public async Task RunAsync(Func<Task> action)
    {
        if (Busy) return; Busy = true; actionCancellation = new();
        try { await action(); }
        catch (OperationCanceledException) { Status = "ยกเลิกแล้ว รายการที่ทำสำเร็จก่อนหน้านี้ยังคงมีผล"; }
        catch (Exception ex) { Status = ex.Message; interaction.Show("ทำรายการไม่สำเร็จ", ex.Message); }
        finally { actionCancellation.Dispose(); actionCancellation = null; Busy = false; }
    }
    private void RefreshHistory() => Replace(History, store.History());
    private static void Replace<T>(BulkObservableCollection<T> target, IEnumerable<T> source) => target.ReplaceAll(source);
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public void Dispose() { SaveSettings(); actionCancellation?.Cancel(); store.Dispose(); }
}
