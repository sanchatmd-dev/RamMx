using System.Diagnostics;
using Microsoft.Win32;

namespace RamMx.Windows;

public static class WindowsLinks
{
    public static void InstalledApps() => Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true });
    public static void StartupApps() => Process.Start(new ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true });
    public static void Storage() => Process.Start(new ProcessStartInfo("ms-settings:storagesense") { UseShellExecute = true });
    public static void Security() => Process.Start(new ProcessStartInfo("windowsdefender:") { UseShellExecute = true });
    public static void ShowFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("ไม่พบไฟล์", path);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = true };
        start.Arguments = "/select,\"" + path.Replace("\"", "") + "\""; Process.Start(start);
    }
    public static void SetStartup(bool enabled, string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable)) throw new ArgumentException("Executable ไม่ถูกต้อง");
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("RamMx", "\"" + executable + "\" --tray", RegistryValueKind.String);
        else key.DeleteValue("RamMx", false);
    }
}
