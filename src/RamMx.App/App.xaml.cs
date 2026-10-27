using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using RamMx.Core;
using RamMx.Windows;

namespace RamMx.App;

public partial class App : System.Windows.Application
{
    private Mutex? singleInstance;
    private EventWaitHandle? activation;
    private RegisteredWaitHandle? activationWait;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.FirstOrDefault() == "--broker")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                var (pipe, request) = await ElevationProtocol.ConnectHelperAsync(e.Args, timeout.Token);
                using (pipe)
                {
                    var all = await Task.Run(() => new TelemetryService().ReadProcesses());
                    var targets = request.Targets.Select(t => all.FirstOrDefault(p => p.Identity == t)).ToArray();
                    ActionResult result;
                    if (targets.Any(p => p == null)) result = new(OperationStatus.Cancelled, "Target เปลี่ยนหลัง preview");
                    else
                    {
                        var text = string.Join("\n", targets.Select(t => $"{t!.Name} (PID {t.Identity.Pid})\n{t.Identity.Path}"));
                        var title = request.Operation == "ForceStop" ? "ยืนยัน Force stop ด้วยสิทธิ์ผู้ดูแล" : "ยืนยันสแกนด้วย Defender";
                        var consequence = request.Operation == "ForceStop" ? "งานที่ยังไม่บันทึกอาจสูญหาย" : "Defender อาจกักกันไฟล์ตามนโยบายของ Windows";
                        var confirm = new ConfirmWindow(title, text + "\n\n" + consequence);
                        if (confirm.ShowDialog() != true) result = new(OperationStatus.Cancelled, "ผู้ใช้ยกเลิกใน elevated helper");
                        else
                        {
                            ElevationProtocol.Validate(request, request.Nonce);
                            result = request.Operation == "ForceStop" ? await new ProcessActions().ExecuteAsync(targets.Select(t => t!).ToArray(), true, timeout.Token)
                                : await DefenderService.ScanAsync(targets[0]!, timeout.Token);
                        }
                    }
                    await ElevationProtocol.WriteAsync(pipe, result, timeout.Token);
                }
            }
            catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "RamMx helper", MessageBoxButton.OK, MessageBoxImage.Information); }
            Shutdown(); return;
        }
        var activationName = "Local\\RamMx-Activate-" + Environment.UserName;
        singleInstance = new Mutex(true, "Local\\RamMx-" + Environment.UserName, out var created);
        if (!created)
        {
            try { using var signal = EventWaitHandle.OpenExisting(activationName); signal.Set(); }
            catch (WaitHandleCannotBeOpenedException) { System.Windows.MessageBox.Show("RamMx กำลังเริ่มต้น โปรดลองเปิดอีกครั้ง", "RamMx"); }
            Shutdown(); return;
        }
        activation = new EventWaitHandle(false, EventResetMode.AutoReset, activationName);
        activationWait = ThreadPool.RegisterWaitForSingleObject(activation, (_, _) =>
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() => { if (MainWindow is MainWindow window) window.RestoreWindow(); });
        }, null, Timeout.Infinite, false);
        DispatcherUnhandledException += (_, args) => { System.Windows.MessageBox.Show(args.Exception.Message, "RamMx — เกิดข้อผิดพลาด"); args.Handled = true; };
        try
        {
            var window = new MainWindow(); MainWindow = window; window.Show();
            await window.InitializeAsync();
            if (e.Args.Contains("--tray")) window.ShowInTray();
        }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "RamMx เปิดไม่ได้"); Shutdown(); }
    }
    public static void SetTheme(string theme)
    {
        var colors = theme == "Light"
            ? new[] { "#F1F4F7", "#FFFFFF", "#E5EBF0", "#15212A", "#526276", "#C4CFDA", "#48BCA5" }
            : new[] { "#11151C", "#1A202A", "#232C38", "#EDF3FA", "#ADB9CA", "#344052", "#70DDC7" };
        var names = new[] { "BackgroundBrush", "SurfaceBrush", "RaisedBrush", "TextBrush", "MutedBrush", "BorderBrush", "AccentBrush" };
        for (int i = 0; i < names.Length; i++) Current.Resources[names[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
    }
    protected override void OnExit(ExitEventArgs e) { activationWait?.Unregister(null); activation?.Dispose(); singleInstance?.Dispose(); base.OnExit(e); }
}
