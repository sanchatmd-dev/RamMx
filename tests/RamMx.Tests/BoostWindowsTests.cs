using System.Diagnostics;
using RamMx.Core;
using RamMx.Windows;
using Xunit;
using Xunit.Abstractions;

namespace RamMx.Tests;

public sealed class BoostWindowsTests(ITestOutputHelper output)
{
    private sealed class Journal : IBoostJournal
    {
        public Dictionary<string, BoostEntry> Entries { get; } = new();
        public bool RejectWrites { get; set; }
        public bool RejectRemovals { get; set; }
        public IReadOnlyList<BoostEntry> PendingBoost() => Entries.Values.ToArray();
        public void SaveBoost(BoostEntry entry) { if (RejectWrites) throw new IOException("Test journal failure"); Entries[entry.Identity.InstanceKey] = entry; }
        public void RemoveBoost(string key) { if (RejectRemovals) throw new IOException("Test journal removal failure"); Entries.Remove(key); }
    }
    private static Process Fixture(string? probe = null)
    {
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "MemoryFixture", "bin", "Release", "net10.0-windows", "MemoryFixture.exe"));
        var start = new ProcessStartInfo(path) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--background");
        if (probe != null) { start.ArgumentList.Add("--probe"); start.ArgumentList.Add(probe); }
        return Process.Start(start)!;
    }
    [Theory] [InlineData(1u)] [InlineData(2u)] [InlineData(4u)]
    public async Task Boost_reclaims_resident_fixture_pages_once_and_preserves_data(uint original)
    {
        string probe = Path.Combine(Path.GetTempPath(), "RamMx-boost-probe-" + Guid.NewGuid().ToString("N"));
        using var fixture = Fixture(probe);
        var journal = new Journal(); BoostService? service = null;
        try
        {
            await Task.Delay(1000);
            var telemetry = new TelemetryService();
            // Only the disposable fixture gets synthetic quiet history/ancestry.
            // Its memory readings and every native action use the real process.
            ProcessSample Sample() => telemetry.ReadProcesses().Single(p => p.Identity.Pid == fixture.Id) with
            { AppKey = BoostTests.App.AppKey, ParentPid = 10, TotalCpuTicks = 0, IoBytes = 0, IsForeground = false, Role = AppRole.UserApp };
            var sample = Sample();
            using var handle = BoostProcessActions.Open(sample.Identity, true);
            BoostProcessActions.SetPriority(handle, original);
            var settings = BoostTests.Settings; var now = BoostTests.Start;
            service = new BoostService(journal, () => [BoostTests.Desktop, Sample()], () => now);
            for (int second = 0; second <= 300; second += 10)
            { now = BoostTests.Start.AddSeconds(second); service.Observe([BoostTests.Desktop, sample], settings, now); }
            var group = new ProcessGroup { AppKey = sample.AppKey, Name = sample.Name, Members = [sample] };
            var result = service.Start([group], settings, true);
            Assert.Equal(OperationStatus.Completed, result.Status);
            var memory = Assert.Single(result.BoostMemory!.Processes);
            Assert.NotNull(memory.Before); Assert.NotNull(memory.After);
            output.WriteLine($"Priority {original}: private resident {memory.Before / 1048576d:F2} → {memory.After / 1048576d:F2} MiB");
            Assert.True((decimal)memory.Before.Value - memory.After.Value > 8 * 1048576, result.BoostMemory.Summary);
            Assert.Equal(Math.Min(original, 2u), BoostProcessActions.ReadPriority(handle));
            Assert.Equal(OperationStatus.Partial, service.Start([group], settings, true).Status);
            Assert.Single(journal.Entries); // No repeated trim while this Boost owns the process.
            await Task.Delay(2000);
            var settled = Sample();
            output.WriteLine($"After 2 seconds: {settled.PrivateWorkingSet / 1048576d:F2} MiB");
            Assert.True((decimal)memory.Before.Value - settled.PrivateWorkingSet!.Value > 8 * 1048576);
            Assert.Equal(OperationStatus.Completed, service.RestoreAll().Status);
            Assert.Equal(original, BoostProcessActions.ReadPriority(handle)); Assert.Empty(journal.Entries);
            File.WriteAllText(probe + ".request", "read pages");
            for (int i = 0; i < 100 && !File.Exists(probe + ".result"); i++) await Task.Delay(100);
            Assert.Equal("intact", File.ReadAllText(probe + ".result"));
            Assert.False(fixture.HasExited);
            output.WriteLine("Fixture read back every allocated page: intact; original priority restored.");
        }
        finally
        {
            service?.RestoreAll();
            if (!fixture.HasExited) { fixture.Kill(); fixture.WaitForExit(3000); }
            File.Delete(probe + ".request"); File.Delete(probe + ".result");
        }
    }
    [Fact] public async Task Memory_priority_and_trim_preserve_live_fixture_and_restore_exact_value()
    {
        using var fixture = Fixture();
        try
        {
            await Task.Delay(750);
            var sample = new TelemetryService().ReadProcesses().Single(p => p.Identity.Pid == fixture.Id);
            Assert.Equal(Environment.ProcessId, sample.ParentPid);
            Assert.NotNull(sample.IoBytes); Assert.NotNull(sample.TotalCpuTicks);
            using var handle = BoostProcessActions.Open(sample.Identity, true);
            // Hosted runners may launch children with Low priority already; establish the fixture's test baseline.
            BoostProcessActions.SetPriority(handle, 4);
            uint before = BoostProcessActions.ReadPriority(handle);
            Assert.Equal(4u, before);
            var entry = new BoostEntry(sample.Identity, sample.Name, sample.AppKey, "ทั่วไป", before, 2);
            var journal = new Journal(); journal.SaveBoost(entry);
            BoostProcessActions.SetPriority(handle, 2); Assert.Equal(2u, BoostProcessActions.ReadPriority(handle));
            BoostProcessActions.Trim(handle); Assert.False(fixture.HasExited);
            var result = new BoostService(journal).RestoreAll();
            Assert.Equal(OperationStatus.Completed, result.Status); Assert.Equal(before, BoostProcessActions.ReadPriority(handle)); Assert.Empty(journal.Entries);
            Assert.Throws<StaleBoostTargetException>(() => BoostProcessActions.Open(sample.Identity with { CreationFileTime = sample.Identity.CreationFileTime - 1 }));
        }
        finally { if (!fixture.HasExited) { fixture.Kill(); fixture.WaitForExit(3000); } }
    }
    [Fact] public async Task Start_rechecks_plan_journals_before_change_and_restores_when_foreground()
    {
        using var fixture = Fixture();
        try
        {
            await Task.Delay(750);
            var real = new TelemetryService().ReadProcesses().Single(p => p.Identity.Pid == fixture.Id);
            // Simulated quiet history/desktop parent; only this disposable fixture receives native mutations.
            var sample = real with { AppKey = BoostTests.App.AppKey, ParentPid = 10, TotalCpuTicks = 0, IoBytes = 0, IsForeground = false, Role = AppRole.UserApp };
            var settings = BoostTests.Settings; var now = BoostTests.Start;
            var journal = new Journal(); var service = new BoostService(journal, () => [BoostTests.Desktop, sample], () => now);
            for (int i = 0; i <= 300; i += 10) { now = BoostTests.Start.AddSeconds(i); service.Observe([BoostTests.Desktop, sample], settings, now); }
            var group = new ProcessGroup { AppKey = sample.AppKey, Name = sample.Name, Members = [sample] };
            using var handle = BoostProcessActions.Open(real.Identity);
            BoostProcessActions.SetPriority(handle, 2);
            Assert.Equal(OperationStatus.Partial, service.Start([group], settings, false).Status);
            Assert.Empty(journal.Entries); // Priority-only mode does not claim already-low targets.
            BoostProcessActions.SetPriority(handle, 4);
            uint original = BoostProcessActions.ReadPriority(handle);
            string fixtureName = sample.Name;
            foreach (string protectedName in new[] { "Codex", "Claude", "ChatGPT", "chrome", "node" })
            {
                sample = sample with { Name = protectedName };
                Assert.Equal(OperationStatus.Partial, service.Start([group], settings, true).Status);
                Assert.Equal(original, BoostProcessActions.ReadPriority(handle)); Assert.Empty(journal.Entries);
            }
            sample = sample with { Name = fixtureName };
            journal.RejectWrites = true;
            Assert.Throws<IOException>(() => service.Start([group], settings, false));
            Assert.Equal(original, BoostProcessActions.ReadPriority(handle));
            journal.RejectWrites = false;
            var result = service.Start([group], settings, false);
            Assert.Equal(OperationStatus.Completed, result.Status); Assert.Equal(2u, BoostProcessActions.ReadPriority(handle));
            Assert.Single(journal.Entries);
            // Repeated Boost cannot trim or mutate a process already owned by the session.
            Assert.Equal(OperationStatus.Partial, service.Start([group], settings, true).Status);
            now = now.AddSeconds(10); sample = sample with { IsForeground = true };
            service.Observe([BoostTests.Desktop, sample], settings, now);
            Assert.Equal(original, BoostProcessActions.ReadPriority(handle)); Assert.Empty(journal.Entries);
            Assert.Equal(OperationStatus.Partial, service.Start([group], settings, true).Status);
        }
        finally { if (!fixture.HasExited) { fixture.Kill(); fixture.WaitForExit(3000); } }
    }
    [Fact] public async Task Recovery_skips_reused_identity_and_preserves_external_priority_changes()
    {
        using var fixture = Fixture();
        try
        {
            await Task.Delay(750);
            var sample = new TelemetryService().ReadProcesses().Single(p => p.Identity.Pid == fixture.Id);
            using var handle = BoostProcessActions.Open(sample.Identity);
            BoostProcessActions.SetPriority(handle, 4);
            uint original = BoostProcessActions.ReadPriority(handle);
            var journal = new Journal();
            var entry = new BoostEntry(sample.Identity, sample.Name, sample.AppKey, "ทั่วไป", original, 2);
            journal.SaveBoost(entry); BoostProcessActions.SetPriority(handle, 3);
            new BoostService(journal).RestoreAll(); Assert.Equal(3u, BoostProcessActions.ReadPriority(handle));
            journal.SaveBoost(entry with { Identity = entry.Identity with { CreationFileTime = entry.Identity.CreationFileTime - 1 } });
            var recovery = new BoostService(journal); journal.RejectRemovals = true;
            Assert.Equal(OperationStatus.Partial, recovery.RestoreAll().Status); Assert.Single(journal.Entries);
            journal.RejectRemovals = false;
            Assert.Equal(OperationStatus.Completed, recovery.RestoreAll().Status);
            Assert.Equal(3u, BoostProcessActions.ReadPriority(handle)); Assert.Empty(journal.Entries);
            BoostProcessActions.SetPriority(handle, original);
        }
        finally { if (!fixture.HasExited) { fixture.Kill(); fixture.WaitForExit(3000); } }
    }
}
