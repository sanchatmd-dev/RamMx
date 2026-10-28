using RamMx.Core;
using Xunit;

namespace RamMx.Tests;

public sealed class BoostMemoryReportTests
{
    [Fact] public void Exited_reused_and_unreadable_targets_are_not_counted_as_reclaimed()
    {
        var old = BoostTests.App with { PrivateWorkingSet = 32 * 1048576 };
        var reused = old with { Identity = old.Identity with { CreationFileTime = 99 }, PrivateWorkingSet = 0 };
        var result = BoostMemoryReport.Capture([old], [reused], [old.Identity]);
        Assert.Null(Assert.Single(result.Processes).After);
        Assert.Contains("อ่าน RAM", result.Summary);
        Assert.Null(Assert.Single(BoostMemoryReport.Capture([old], [old with { PrivateWorkingSet = null }], [old.Identity]).Processes).After);
    }
    [Fact] public void Reports_net_change_only_for_acted_on_identities_and_marks_partial_coverage()
    {
        var a = BoostTests.App with { PrivateWorkingSet = 32 * 1048576 };
        var b = a with { Identity = a.Identity with { Pid = 21 }, PrivateWorkingSet = 16 * 1048576 };
        var ignored = a with { Identity = a.Identity with { Pid = 22 }, PrivateWorkingSet = 1000 * 1048576 };
        var report = BoostMemoryReport.Capture([a, b, ignored], [a with { PrivateWorkingSet = 8 * 1048576 }, b with { PrivateWorkingSet = 48 * 1048576 }], [a.Identity, b.Identity]);
        Assert.Equal(2, report.Processes.Count); Assert.Contains("เพิ่มขึ้น 8.00 MiB", report.Summary);
        report = BoostMemoryReport.Capture([a, b], [a], [a.Identity, b.Identity]);
        Assert.Contains("1/2", report.Summary); Assert.Contains("0.00 MiB", report.Summary);
        Assert.Empty(BoostMemoryReport.Capture([a], [a], []).Processes);
    }
    [Theory] [InlineData(1u)] [InlineData(2u)]
    public void Low_priority_trim_journal_round_trips_without_raising_priority(uint priority)
    {
        string root = Path.Combine(Path.GetTempPath(), "RamMx.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var entry = new BoostEntry(BoostTests.App.Identity, "fixture", "fixture", "ทั่วไป", priority, priority);
            using (var store = new RamMx.Persistence.StateStore(root))
            {
                store.SaveBoost(entry);
                var id = store.Begin("Boost RAM", "fixture");
                var old = BoostTests.App with { PrivateWorkingSet = 32 * 1048576 };
                var report = BoostMemoryReport.Capture([old], [old with { PrivateWorkingSet = 1048576 }], [old.Identity]);
                store.Complete(id, new(OperationStatus.Completed, "Trim", BoostMemory: report));
            }
            using (var store = new RamMx.Persistence.StateStore(root))
            {
                Assert.Equal(entry, Assert.Single(store.PendingBoost()));
                Assert.Contains("ลดลง 31.00 MiB", Assert.Single(store.History()).Message);
                Assert.Throws<InvalidDataException>(() => store.SaveBoost(entry with { AppliedPriority = 5 }));
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (PathPolicy.IsInside(Path.Combine(Path.GetTempPath(), "RamMx.Tests"), root)) Directory.Delete(root, true);
        }
    }
}
