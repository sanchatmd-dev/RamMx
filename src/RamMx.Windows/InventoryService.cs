using Microsoft.Win32;
using RamMx.Core;
using Windows.Management.Deployment;

namespace RamMx.Windows;

public sealed class InventoryService
{
    public IReadOnlyList<InstalledApp> Read()
    {
        var results = new List<InstalledApp>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall == null) continue;
            foreach (var sub in uninstall.GetSubKeyNames())
            {
                try
                {
                    using var key = uninstall.OpenSubKey(sub);
                    if (key == null) continue;
                    string Get(string name) => key.GetValue(name)?.ToString()?.Trim() ?? "";
                    var name = Get("DisplayName"); if (name.Length == 0) continue;
                    var cmd = Get("UninstallString"); var location = Get("InstallLocation").Trim('"');
                    var product = Get("WindowsInstaller") == "1" && Guid.TryParse(sub, out var guid) ? "{" + guid.ToString().ToUpperInvariant() + "}" : null;
                    bool protect = Get("SystemComponent") == "1" || Get("ParentKeyName").Length > 0 || IsProtectedInstall(name, location);
                    var scope = hive == RegistryHive.CurrentUser ? "Current user" : "Machine";
                    results.Add(new($"registry:{hive}:{view}:{sub}", name, Get("DisplayVersion"), Get("Publisher"), location, cmd,
                        product == null ? "EXE" : "MSI", scope, product, null,
                        long.TryParse(Get("EstimatedSize"), out var size) ? size : null, protect, $"{hive}|{view}|{sub}"));
                }
                catch (System.Security.SecurityException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        try
        {
            var manager = new PackageManager();
            foreach (var package in manager.FindPackagesForUser(""))
            {
                try
                {
                    if (package.IsResourcePackage) continue;
                    var id = package.Id;
                    var v = id.Version;
                    var name = package.DisplayName;
                    if (string.IsNullOrWhiteSpace(name)) name = id.Name;
                    bool protect = package.IsFramework || package.IsBundle || IsProtectedInstall(name, package.InstalledLocation.Path) || id.Name.StartsWith("Microsoft.Windows", StringComparison.OrdinalIgnoreCase) || id.Name.StartsWith("Microsoft.AAD", StringComparison.OrdinalIgnoreCase) || id.Name.StartsWith("Microsoft.SecHealth", StringComparison.OrdinalIgnoreCase);
                    results.Add(new($"package:{id.FamilyName}", name, $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}", package.PublisherDisplayName,
                        package.InstalledLocation.Path, "", "MSIX", "Current user", null, id.FullName, null, protect, id.FamilyName));
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { }
        return results.GroupBy(a => a.Key).Select(g => g.First()).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static bool IsProtectedInstall(string name, string location)
    {
        if (SharedComponent(name)) return true;
        if (string.IsNullOrWhiteSpace(location)) return false;
        try { return PathPolicy.IsSameOrInside(Environment.GetFolderPath(Environment.SpecialFolder.Windows), location); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return true; }
    }
    public static bool SharedComponent(string name) => new[] { "Microsoft Visual C++", "Microsoft .NET", "Windows SDK", "Windows Driver", "WebView2 Runtime", "Windows App Runtime", "Microsoft Edge Update", "Microsoft Defender", "Windows Security", "CrowdStrike", "Sentinel Agent", "Sophos", "Kaspersky", "ESET Security", "Malwarebytes", "Bitdefender", "McAfee", "Norton Security", "Trend Micro" }
        .Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<StartupEntry> ReadStartup()
    {
        var entries = new List<StartupEntry>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (key == null) continue;
            entries.AddRange(key.GetValueNames().Select(n => new StartupEntry(n, $"Run / {view}", key.GetValue(n)?.ToString() ?? "", hive.ToString())));
        }
        foreach (var folder in new[] { Environment.SpecialFolder.Startup, Environment.SpecialFolder.CommonStartup })
        {
            var path = Environment.GetFolderPath(folder);
            if (Directory.Exists(path)) entries.AddRange(Directory.EnumerateFiles(path).Select(p => new StartupEntry(Path.GetFileName(p), "Startup folder", p, folder.ToString())));
        }
        return entries;
    }
}
