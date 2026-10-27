using System.Diagnostics;
using System.Runtime.InteropServices;
using RamMx.Core;

namespace RamMx.Windows;

public sealed class TelemetryService
{
    private readonly Dictionary<string, (long Cpu, long Time)> previousCpu = new();
    private readonly string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private readonly int ownSession = Process.GetCurrentProcess().SessionId;
    private IReadOnlyList<InstalledApp> inventory = [];
    private Dictionary<string, InstalledApp> packages = new(StringComparer.OrdinalIgnoreCase);
    private (string Root, InstalledApp App)[] locations = [];
    public IReadOnlyList<InstalledApp> Inventory
    {
        get => inventory;
        set
        {
            inventory = value;
            packages = value.Where(a => a.PackageFullName != null).GroupBy(a => a.PackageFullName!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            locations = value.Select(a => (Root: NormalizeInstallRoot(a.InstallLocation), App: a))
                .Where(a => a.Root != null).Select(a => (Root: a.Root!, a.App)).OrderByDescending(a => a.Root.Length).ToArray();
        }
    }

    private static string? NormalizeInstallRoot(string path)
    {
        try { return path.Length > 3 && Path.IsPathFullyQualified(path) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar : null; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    public MemorySnapshot ReadMemory()
    {
        if (!Native.GetPerformanceInfo(out var info, (uint)Marshal.SizeOf<Native.PerformanceInfo>())) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        ulong page = info.PageSize; ulong installed = 0;
        if (Native.GetPhysicallyInstalledSystemMemory(out var kb)) installed = kb * 1024;
        var drive = new DriveInfo(Path.GetPathRoot(windows)!);
        return new(DateTimeOffset.Now, (ulong)info.PhysicalTotal * page, (ulong)info.PhysicalAvailable * page,
            (ulong)info.CommitTotal * page, (ulong)info.CommitLimit * page, (ulong)info.KernelPaged * page, (ulong)info.KernelNonpaged * page,
            (ulong)info.SystemCache * page, installed, (ulong)drive.TotalSize, (ulong)drive.AvailableFreeSpace);
    }

    public IReadOnlyList<ProcessSample> ReadProcesses()
    {
        var results = new List<ProcessSample>();
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint foreground);
        var visiblePids = new HashSet<int>();
        Native.EnumWindows((window, _) => { if (Native.IsWindowVisible(window)) { Native.GetWindowThreadProcessId(window, out var owner); visiblePids.Add((int)owner); } return true; }, 0);
        var now = Stopwatch.GetTimestamp();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    using var handle = Native.OpenProcess(0x1410, false, process.Id);
                    using var limited = handle.IsInvalid ? Native.OpenProcess(0x1000, false, process.Id) : null;
                    var query = limited ?? handle;
                    string path = query.IsInvalid ? "" : Native.ImagePath(query);
                    if (query.IsInvalid || !Native.GetProcessTimes(query, out long creationValue, out _, out long kernelValue, out long userValue))
                    { creationValue = 0; kernelValue = 0; userValue = 0; }
                    long creation = creationValue, kernel = kernelValue, user = userValue;
                    bool? critical = !query.IsInvalid && Native.IsProcessCritical(query, out bool value) ? value : null;
                    bool? protection = !query.IsInvalid && Native.GetProcessInformation(query, 7, out var protectionInfo, 4) ? protectionInfo.Level != 0xfffffffe : null;
                    var counters = new Native.MemoryCounters { Size = (uint)Marshal.SizeOf<Native.MemoryCounters>() };
                    bool ex2 = !query.IsInvalid && Native.GetProcessMemoryInfo(query, ref counters, counters.Size);
                    bool readable = ex2;
                    if (!ex2)
                    {
                        counters.Size = (uint)(8 + 9 * IntPtr.Size);
                        readable = Native.GetProcessMemoryInfo(query, ref counters, counters.Size);
                    }
                    var package = query.IsInvalid ? null : Native.PackageName(query);
                    InstalledApp? installed = package != null ? packages.GetValueOrDefault(package) : null;
                    if (installed == null && path.Length > 0) installed = locations.FirstOrDefault(a => path.StartsWith(a.Root, StringComparison.OrdinalIgnoreCase)).App;
                    var role = Classify(process.ProcessName, path, process.SessionId, critical, protection);
                    var description = Labels.Role(role);
                    var displayName = installed?.Name ?? process.ProcessName;
                    // Do not inspect executable files or enumerate windows per process in the polling loop.
                    description = process.ProcessName.ToLowerInvariant() switch { "explorer" => "Windows Explorer — เดสก์ท็อปและหน้าต่างไฟล์", "dwm" => "Desktop Window Manager — จัดองค์ประกอบหน้าจอ", "svchost" => "Host ของ Windows services — ไม่ควรหยุดทั้ง host", "msmpeng" => "Microsoft Defender Antivirus", "memory compression" => "Windows memory compression", _ => description };
                    if (role != AppRole.UserApp) displayName = process.ProcessName;
                    string key = installed?.Key ?? (path.Length > 0 ? $"file:{path.ToUpperInvariant()}" : $"unknown:{process.Id}:{creation}");
                    // Shared runtimes remain individually visible; a shared installation path does not establish workload ownership.
                    if (role == AppRole.SharedRuntime || role == AppRole.ServiceHost) key = $"runtime:{path}:{process.Id}:{creation}";
                    var identity = new ProcessIdentity(process.Id, creation, path, process.SessionId);
                    var cpuTime = kernel + user; double cpu = 0;
                    if (previousCpu.TryGetValue(identity.InstanceKey, out var old) && now > old.Time)
                        cpu = Math.Clamp((cpuTime - old.Cpu) / 10000000d / ((now - old.Time) / (double)Stopwatch.Frequency) / Environment.ProcessorCount * 100, 0, 100);
                    previousCpu[identity.InstanceKey] = (cpuTime, now);
                    results.Add(new(identity, process.ProcessName, key, displayName, role, counters.WorkingSet,
                        ex2 ? (ulong)counters.PrivateWorkingSet : null, counters.PrivateUsage, cpu, critical, protection,
                        visiblePids.Contains(process.Id), process.Id == foreground, package, description, readable));
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or System.NotSupportedException) { }
            }
        }
        var keys = results.Select(x => x.Identity.InstanceKey).ToHashSet();
        foreach (var stale in previousCpu.Keys.Where(k => !keys.Contains(k)).ToArray()) previousCpu.Remove(stale);
        return results;
    }

    public static IReadOnlyList<ProcessGroup> Group(IReadOnlyList<ProcessSample> samples, UserSettings settings) => samples
        .GroupBy(s => s.AppKey).Select(g => new ProcessGroup { AppKey = g.Key, Name = g.First().DisplayName, Members = g.ToArray(), Keep = settings.KeepApps.Contains(g.Key) })
        .OrderByDescending(g => g.PrivateBytes).ToArray();

    private AppRole Classify(string name, string path, int session, bool? critical, bool? protection)
    {
        if (name.Contains("RamMx", StringComparison.OrdinalIgnoreCase)) return AppRole.Utility;
        if (new[] { "MsMpEng", "MpDefenderCoreService", "NisSrv", "SecurityHealthService", "SecurityHealthSystray", "Sense", "MsSense" }.Contains(name, StringComparer.OrdinalIgnoreCase)) return AppRole.Security;
        if (name.Equals("svchost", StringComparison.OrdinalIgnoreCase)) return AppRole.ServiceHost;
        if (critical == true || protection == true || (path.Length > 0 && SafeInside(windows, path))) return AppRole.WindowsComponent;
        if (new[] { "node", "python", "pythonw", "java", "javaw", "dotnet", "msedgewebview2", "conhost", "pwsh", "powershell", "cmd" }.Contains(name, StringComparer.OrdinalIgnoreCase)) return AppRole.SharedRuntime;
        if (session != ownSession) return AppRole.ServiceHost;
        if (path.Length == 0 || path.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase)) return AppRole.Unknown;
        return AppRole.UserApp;
    }
    private static bool SafeInside(string root, string path) { try { return PathPolicy.IsInside(root, path); } catch { return false; } }
}
