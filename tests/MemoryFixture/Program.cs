using System.Windows;
using Microsoft.Win32;

namespace MemoryFixture;

internal static class Program
{
    private static byte[]? allocation;
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--uninstall-fixture"))
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall", true);
            root?.DeleteSubKeyTree("RamMx.IntegrationFixture", false);
            return;
        }
        allocation = new byte[32 * 1024 * 1024];
        for (int i = 0; i < allocation.Length; i += 4096) allocation[i] = 1;
        var app = new Application();
        // Test-only handshake: prove trimmed pages can be read back without losing data.
        int probeIndex = Array.IndexOf(args, "--probe");
        if (probeIndex >= 0 && probeIndex + 1 < args.Length)
        {
            string probe = args[probeIndex + 1];
            var check = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            check.Tick += (_, _) =>
            {
                if (!System.IO.File.Exists(probe + ".request")) return;
                check.Stop(); bool intact = true;
                for (int i = 0; i < allocation.Length; i += 4096) intact &= allocation[i] == 1;
                System.IO.File.WriteAllText(probe + ".result", intact ? "intact" : "corrupt");
            };
            check.Start();
        }
        if (args.Contains("--background"))
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var lifetime = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
            lifetime.Tick += (_, _) => { lifetime.Stop(); app.Shutdown(); };
            lifetime.Start(); app.Run(); GC.KeepAlive(allocation); return;
        }
        var window = new Window { Title = "RamMx disposable memory fixture", Width = 450, Height = 180, Content = new System.Windows.Controls.TextBlock { Text = "Disposable test process · 32 MiB\nNo user data. Safe to close.", Margin = new Thickness(24), FontSize = 18 } };
        if (args.Contains("--refuse-close")) window.Closing += (_, e) => e.Cancel = true;
        app.Run(window); GC.KeepAlive(allocation);
    }
}
