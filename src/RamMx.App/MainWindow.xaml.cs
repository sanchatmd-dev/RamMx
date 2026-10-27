using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace RamMx.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel;
    private readonly DispatcherTimer timer = new();
    private readonly System.Windows.Forms.NotifyIcon tray;
    private bool sampling, exitRequested;
    public MainWindow()
    {
        InitializeComponent();
        viewModel = new(new DialogService(() => this)); DataContext = viewModel;
        Width = Math.Min(viewModel.Settings.WindowWidth, SystemParameters.WorkArea.Width - 20);
        Height = Math.Min(viewModel.Settings.WindowHeight, SystemParameters.WorkArea.Height - 20);
        tray = new System.Windows.Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "RamMx — RAM และแอป", Visible = false };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("เปิด RamMx", null, (_, _) => Dispatcher.Invoke(RestoreWindow));
        menu.Items.Add("ออกจาก RamMx", null, (_, _) => Dispatcher.Invoke(() => { exitRequested = true; Close(); }));
        tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreWindow);
        timer.Tick += async (_, _) =>
        {
            if (sampling || viewModel.Busy) return;
            sampling = true;
            try { await viewModel.SampleAsync(updateVisuals: IsVisible && WindowState != WindowState.Minimized); }
            finally { sampling = false; timer.Interval = TimeSpan.FromSeconds(IsVisible && WindowState != WindowState.Minimized ? viewModel.Settings.RefreshSeconds : 10); }
        };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized && viewModel.MinimizeToTray) ShowInTray(); };
        Closing += OnClosing;
        Closed += (_, _) => { timer.Stop(); tray.Dispose(); viewModel.Dispose(); };
    }
    public async Task InitializeAsync() { await viewModel.InitializeAsync(); timer.Interval = TimeSpan.FromSeconds(viewModel.RefreshSeconds); timer.Start(); }
    public void ShowInTray() { tray.Visible = true; Hide(); timer.Interval = TimeSpan.FromSeconds(10); }
    public void RestoreWindow() { Show(); WindowState = WindowState.Normal; Activate(); tray.Visible = false; timer.Interval = TimeSpan.FromSeconds(viewModel.RefreshSeconds); }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (viewModel.Busy) { e.Cancel = true; System.Windows.MessageBox.Show("มีรายการกำลังทำงานอยู่ กรุณารอผลหรือกดยกเลิกการรอก่อนปิด", "RamMx"); return; }
        viewModel.Settings.WindowWidth = ActualWidth; viewModel.Settings.WindowHeight = ActualHeight;
        if (viewModel.MinimizeToTray && !exitRequested) { e.Cancel = true; ShowInTray(); viewModel.SaveSettings(); }
    }
    private void GoProcesses(object sender, RoutedEventArgs e) => viewModel.Page = 1;
    private void GoStorage(object sender, RoutedEventArgs e) => viewModel.Page = 3;
}
