using System.Diagnostics;
using RamMx.Core;
using RamMx.Windows;
using Xunit;
using Xunit.Abstractions;
using Value = RamMx.Windows.PerformanceMemoryReader.CounterValue;

namespace RamMx.Tests;

public sealed class PerformanceMemoryTests(ITestOutputHelper output)
{
    [Fact]
    public void Counter_snapshot_reads_real_private_resident_memory_without_process_handles()
    {
        var snapshot = PerformanceMemoryReader.Read();
        var own = snapshot[Environment.ProcessId];
        using var process = Process.GetCurrentProcess();
        Assert.True(PerformanceMemoryReader.Matches(own.Instance, process.ProcessName, process.Id));
        Assert.True(own.PrivateWorkingSet > 0);
        Assert.True(own.PrivateWorkingSet <= own.WorkingSet);
        Assert.True(own.PrivateCommit > 0);
    }

    [Fact]
    public void System_process_memory_is_available_when_performance_provider_exposes_it()
    {
        var snapshot = PerformanceMemoryReader.Read();
        Assert.NotEmpty(snapshot);
        var telemetry = new TelemetryService();
        var timer = Stopwatch.StartNew();
        var processes = telemetry.ReadProcesses();
        output.WriteLine($"Sampler: {timer.Elapsed.TotalMilliseconds:F1} ms; private RAM coverage: {processes.Count(p => p.PrivateWorkingSet.HasValue)}/{processes.Count}");
        foreach (var sample in processes.Where(p => new[] { "winlogon", "fontdrvhost", "atieclxx", "dwm" }.Contains(p.Name)))
        {
            if (!snapshot.ContainsKey(sample.Identity.Pid)) continue;
            Assert.NotNull(sample.PrivateWorkingSet);
            Assert.True(sample.MemoryAvailable);
            Assert.True(sample.PrivateWorkingSet <= sample.WorkingSet);
            Assert.False(sample.CanStop);
            output.WriteLine($"{sample.Name}: private WS {sample.PrivateWorkingSet} B; commit {sample.PrivateCommit} B; {sample.MemorySource}");
        }
    }

    [Fact]
    public void Joining_uses_exact_instance_names_and_preserves_zero()
    {
        var ids = Values(("app", 11), ("app#1", 12));
        var result = PerformanceMemoryReader.Join(ids, Values(("app#1", 1000), ("app", 200)),
            Values(("app#1", 300), ("app", 0)), Values(("app#1", 9000), ("app", 4000)));
        Assert.Equal(0UL, result[11].PrivateWorkingSet);
        Assert.Equal(300UL, result[12].PrivateWorkingSet);
        Assert.Equal(9000UL, result[12].PrivateCommit);
        Assert.Equal(1000UL, result[12].WorkingSet);
    }

    [Theory]
    [InlineData(0, -1)] [InlineData(0xc0000bc6, 10)] [InlineData(0, 201)]
    public void Failed_negative_or_inconsistent_values_are_unavailable(uint status, long value)
    {
        var result = PerformanceMemoryReader.Join(Values(("app", 11)), Values(("app", 200)),
            new Dictionary<string, Value> { ["app"] = new(status, value) }, Values(("app", 4000)));
        Assert.Empty(result);
    }

    [Fact]
    public void Missing_counter_or_duplicate_pid_is_not_attributed_to_a_process()
    {
        var ids = Values(("app", 11), ("app#1", 11), ("missing", 12));
        var values = Values(("app", 100), ("app#1", 100));
        Assert.Empty(PerformanceMemoryReader.Join(ids, values, values, values));
        Assert.Empty(PerformanceMemoryReader.Join(ids, values, new Dictionary<string, Value>(), values));
    }

    [Theory]
    [InlineData("dwm:11", "dwm", true)] [InlineData("app:11", "app", true)]
    [InlineData("app:11", "other", false)] [InlineData("app:12", "app", false)]
    [InlineData("DWM:11", "dwm", true)]
    public void Pid_join_also_requires_matching_process_name(string instance, string name, bool expected) =>
        Assert.Equal(expected, PerformanceMemoryReader.Matches(instance, name, 11));

    [Fact]
    public void Repeated_array_names_stay_invalid_instead_of_overwriting()
    {
        var values = new Dictionary<string, Value>();
        PerformanceMemoryReader.AddValue(values, "app:11", new(0, 10));
        PerformanceMemoryReader.AddValue(values, "app:11", new(0, 20));
        PerformanceMemoryReader.AddValue(values, "app:11", new(0, 30));
        Assert.Empty(PerformanceMemoryReader.Join(Values(("app:11", 11)), Values(("app:11", 100)), values, Values(("app:11", 200))));
    }

    [Fact]
    public async Task Warm_query_is_reused_serially_and_reset_reopens_it()
    {
        PerformanceMemoryReader.Reset(); int opened = PerformanceMemoryReader.QueryOpenCount;
        try
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(PerformanceMemoryReader.Read)));
            Assert.All(results, snapshot => Assert.True(snapshot[Environment.ProcessId].PrivateWorkingSet > 0));
            Assert.Equal(opened + 1, PerformanceMemoryReader.QueryOpenCount);
            Assert.NotSame(results[0], results[1]); // Values belong to each collection, not a cached snapshot.
            PerformanceMemoryReader.Reset();
            Assert.True(PerformanceMemoryReader.Read()[Environment.ProcessId].PrivateCommit > 0);
            Assert.Equal(opened + 2, PerformanceMemoryReader.QueryOpenCount);
        }
        finally { PerformanceMemoryReader.Reset(); }
    }

    [Fact]
    public async Task Two_live_processes_with_the_same_executable_name_have_distinct_memory()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "MemoryFixture", "bin", "Release", "net10.0-windows", "MemoryFixture.exe"));
        var fixtures = new List<Process>();
        try
        {
            PerformanceMemoryReader.Reset();
            _ = PerformanceMemoryReader.Read();
            int opened = PerformanceMemoryReader.QueryOpenCount;
            for (int i = 0; i < 2; i++)
                fixtures.Add(Process.Start(new ProcessStartInfo(path, "--background") { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden })!);
            await Task.Delay(1500);
            var snapshot = PerformanceMemoryReader.Read();
            foreach (var fixture in fixtures)
            {
                Assert.False(fixture.HasExited);
                Assert.True(PerformanceMemoryReader.Matches(snapshot[fixture.Id].Instance, fixture.ProcessName, fixture.Id));
                Assert.True(snapshot[fixture.Id].PrivateWorkingSet > 0);
            }
            Assert.NotEqual(snapshot[fixtures[0].Id].Instance, snapshot[fixtures[1].Id].Instance);
            Assert.Equal(opened, PerformanceMemoryReader.QueryOpenCount);
            foreach (var fixture in fixtures) { fixture.Kill(); fixture.WaitForExit(3000); }
            var afterExit = PerformanceMemoryReader.Read();
            foreach (var fixture in fixtures) Assert.DoesNotContain(fixture.Id, afterExit.Keys);
            Assert.Equal(opened, PerformanceMemoryReader.QueryOpenCount);
        }
        finally
        {
            foreach (var fixture in fixtures)
            {
                if (!fixture.HasExited) { fixture.Kill(); fixture.WaitForExit(3000); }
                fixture.Dispose();
            }
            PerformanceMemoryReader.Reset();
        }
    }

    [Fact]
    public void Readable_memory_does_not_grant_stop_permission_or_invent_identity()
    {
        var sample = CoreTests.Sample() with { Identity = new(11, 0, "", 1), Critical = null, Protected = null,
            MemorySource = ProcessMemorySource.PerformanceCounters };
        Assert.False(sample.CanStop);
        var group = new ProcessGroup { AppKey = sample.AppKey, Name = sample.Name, Members = [sample] };
        Assert.NotEqual("อ่านไม่ได้", group.RamLabel);
        var missing = sample with { PrivateWorkingSet = null, WorkingSet = 0, PrivateCommit = 0, MemoryAvailable = false };
        group.Members = [sample, missing];
        Assert.EndsWith(" *", group.RamLabel);
        Assert.Equal("อ่านไม่ได้", missing.MemorySourceLabel);
        group.Members = [missing];
        Assert.Equal("อ่านไม่ได้", group.RamLabel);
    }

    private static Dictionary<string, Value> Values(params (string Name, long Value)[] entries) =>
        entries.ToDictionary(e => e.Name, e => new Value(0, e.Value));
}
