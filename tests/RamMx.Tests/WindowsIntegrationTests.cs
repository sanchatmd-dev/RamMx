using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using RamMx.Core;
using RamMx.Windows;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace RamMx.Tests;

public sealed class WindowsIntegrationTests
{
    [Fact] public void Native_system_counters_have_consistent_units()
    {
        var memory = new TelemetryService().ReadMemory();
        Assert.True(memory.Usable > 1024UL * 1024 * 1024); Assert.True(memory.Available <= memory.Usable);
        Assert.True(memory.Commit <= memory.CommitLimit); Assert.True(memory.DiskFree <= memory.DiskTotal);
    }
    [Fact] public void Current_process_identity_and_memory_can_be_read()
    {
        var process = new TelemetryService().ReadProcesses().Single(p => p.Identity.Pid == Environment.ProcessId);
        Assert.True(process.Identity.CreationFileTime > 0); Assert.True(File.Exists(process.Identity.Path));
        Assert.True(process.WorkingSet > 0); Assert.NotNull(process.PrivateWorkingSet); Assert.True(process.PrivateWorkingSet <= process.WorkingSet);
    }
    [Fact] public async Task Stale_identity_never_stops_current_process()
    {
        var current = new TelemetryService().ReadProcesses().Single(p => p.Identity.Pid == Environment.ProcessId);
        var forged = current with { Identity = current.Identity with { CreationFileTime = current.Identity.CreationFileTime - 1 } };
        Assert.Equal(OperationStatus.Cancelled, (await new ProcessActions().ExecuteAsync([forged], true)).Status);
    }
    [Fact] public void Unsigned_fixture_is_evidence_not_a_malware_verdict()
    {
        var result = new SignatureService().Inspect("fixture", FixturePath());
        Assert.NotEqual(SignatureState.Valid, result.Signature); Assert.Contains(result.Evidence, e => e.Detail.Contains("ไม่ใช่ malware verdict"));
    }
    [Fact] public async Task Graceful_close_ends_only_disposable_fixture()
    {
        using var fixture = StartFixture();
        try
        {
            await WaitWindow(fixture);
            var target = new TelemetryService().ReadProcesses().Single(p => p.Identity.Pid == fixture.Id);
            Assert.True(ActionPolicy.Evaluate(target).Allowed);
            var result = await new ProcessActions().ExecuteAsync([target], false);
            Assert.Equal(OperationStatus.Completed, result.Status); Assert.True(fixture.HasExited);
        }
        finally { StopFixture(fixture); }
    }
    [Fact] public async Task Refused_close_is_not_automatically_forced()
    {
        using var fixture = StartFixture("--refuse-close");
        try
        {
            await WaitWindow(fixture); var target = new TelemetryService().ReadProcesses().Single(p => p.Identity.Pid == fixture.Id);
            var result = await new ProcessActions().ExecuteAsync([target], false); Assert.Equal(OperationStatus.Partial, result.Status); Assert.False(fixture.HasExited);
            result = await new ProcessActions().ExecuteAsync([target], true); Assert.Equal(OperationStatus.Completed, result.Status); Assert.True(fixture.HasExited);
        }
        finally { StopFixture(fixture); }
    }
    [Fact] public async Task Registered_exe_fixture_is_uninstalled_and_verified()
    {
        const string sub = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\RamMx.IntegrationFixture";
        using (var existing = Registry.CurrentUser.OpenSubKey(sub)) Assert.Null(existing); // Never overwrite an existing registration.
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(sub))
            {
                key.SetValue("DisplayName", "RamMx Disposable Integration Fixture"); key.SetValue("DisplayVersion", "1.0");
                key.SetValue("Publisher", "RamMx tests"); key.SetValue("UninstallString", "\"" + FixturePath() + "\" --uninstall-fixture");
            }
            var item = new InventoryService().Read().First(a => a.RegistrationPath.EndsWith("RamMx.IntegrationFixture", StringComparison.Ordinal));
            var result = await new UninstallService().UninstallAsync(item);
            Assert.Equal(OperationStatus.Completed, result.Status);
            using var removed = Registry.CurrentUser.OpenSubKey(sub); Assert.Null(removed);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(sub, false); }
    }
    [Fact] public void Hard_linked_cache_files_are_skipped()
    {
        var root = Path.Combine(Path.GetTempPath(), "RamMx.Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(root, "Cache"));
        try
        {
            var personal = Path.Combine(root, "personal.txt"); File.WriteAllText(personal, "keep");
            Assert.True(CreateHardLink(Path.Combine(root, "Cache", "shared.bin"), personal, 0));
            var recipe = new CacheRecipe("fixture", "fixture", root, ["Cache"], []); var result = new CacheService(recipe).Scan(recipe);
            Assert.Empty(result.Files); Assert.Contains(result.Skipped, s => s.Contains("hard link")); Assert.Equal("keep", File.ReadAllText(personal));
        }
        finally { if (PathPolicy.IsInside(Path.Combine(Path.GetTempPath(), "RamMx.Tests"), root)) Directory.Delete(root, true); }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLink(string path, string existing, nint security);
    private static string FixturePath()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "MemoryFixture", "bin", "Release", "net10.0-windows", "MemoryFixture.exe"));
        Assert.True(File.Exists(path), "Build tests/MemoryFixture in Release before integration tests: " + path); return path;
    }
    private static Process StartFixture(string? argument = null)
    {
        var start = new ProcessStartInfo(FixturePath()) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
        if (argument != null) start.ArgumentList.Add(argument);
        return Process.Start(start)!;
    }
    private static async Task WaitWindow(Process process)
    {
        for (int i = 0; i < 50; i++) { if (process.HasExited) throw new InvalidOperationException("Fixture exited before window"); process.Refresh(); if (process.MainWindowHandle != 0) return; await Task.Delay(100); }
        throw new TimeoutException("Fixture window unavailable");
    }
    private static void StopFixture(Process process) { if (!process.HasExited) { process.Kill(); process.WaitForExit(3000); } }
}
