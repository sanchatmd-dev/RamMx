using System.Diagnostics;
using RamMx.Core;
using RamMx.Windows;
using Xunit;

namespace RamMx.Tests;

public sealed class BoostWindowsTests
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
    private static Process Fixture()
    {
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "MemoryFixture", "bin", "Release", "net10.0-windows", "MemoryFixture.exe"));
        return Process.Start(new ProcessStartInfo(path, "--background") { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden })!;
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
            uint before = BoostProcessActions.ReadPriority(handle);
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
            uint original = BoostProcessActions.ReadPriority(handle);
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
