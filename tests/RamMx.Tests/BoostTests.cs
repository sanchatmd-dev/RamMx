using RamMx.Core;
using RamMx.Persistence;
using Xunit;

namespace RamMx.Tests;

public sealed class BoostTests
{
    internal static readonly DateTimeOffset Start = new(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);
    internal static ProcessSample Desktop => CoreTests.Sample(AppRole.WindowsComponent) with
    { Identity = new(10, 10, @"C:\Windows\explorer.exe", 1), Name = "explorer", AppKey = "desktop", ParentPid = 4, TotalCpuTicks = 0, IoBytes = 0 };
    internal static ProcessSample App => CoreTests.Sample() with { Identity = new(20, 20, @"C:\Apps\Example\example.exe", 1), ParentPid = 10, TotalCpuTicks = 0, IoBytes = 0 };
    internal static UserSettings Settings => new() { BoostIdleMinutes = 5, BoostAllowedApps = new() { ["ทั่วไป"] = [App.AppKey] } };
    private static BoostActivityTracker Quiet(params ProcessSample[] samples)
    {
        var tracker = new BoostActivityTracker();
        for (int second = 0; second <= 300; second += 10) tracker.Observe(Start.AddSeconds(second), samples);
        return tracker;
    }
    private static BoostAssessment Assess(ProcessSample app, UserSettings settings, BoostActivityTracker tracker, DateTimeOffset? time = null, params ProcessSample[] extra) =>
        BoostPolicy.Assess([Desktop, app, .. extra], settings, tracker, time ?? Start.AddMinutes(5)).Single(a => a.Group.AppKey == app.AppKey);

    [Fact] public void Approval_and_continuous_quiet_period_are_both_required()
    {
        var tracker = new BoostActivityTracker(); tracker.Observe(Start, [Desktop, App]);
        Assert.False(Assess(App, Settings, tracker, Start).Eligible);
        var quiet = Quiet(Desktop, App);
        Assert.True(Assess(App, Settings, quiet).Eligible);
        var settings = Settings; settings.BoostAllowedApps.Clear();
        Assert.False(Assess(App, settings, quiet).Eligible);
    }
    [Theory] [InlineData("foreground")] [InlineData("cpu")] [InlineData("io")] [InlineData("unknown")]
    public void Activity_or_missing_measurements_reset_inactivity(string kind)
    {
        var tracker = Quiet(Desktop, App);
        var active = kind switch
        {
            "foreground" => App with { IsForeground = true },
            "cpu" => App with { TotalCpuTicks = 10000000 },
            "io" => App with { IoBytes = 10 * 1024 * 1024 },
            _ => App with { IoBytes = null }
        };
        tracker.Observe(Start.AddSeconds(310), [Desktop, active]);
        Assert.False(Assess(active, Settings, tracker, Start.AddSeconds(310)).Eligible);
    }
    [Fact] public void Gaps_clock_reversal_and_new_instances_do_not_inherit_idle_time()
    {
        var tracker = Quiet(Desktop, App);
        tracker.Observe(Start.AddMinutes(10), [Desktop, App]);
        Assert.Equal(TimeSpan.Zero, tracker.IdleFor(App, Start.AddMinutes(10)));
        tracker.Observe(Start, [Desktop, App]);
        Assert.Equal(TimeSpan.Zero, tracker.IdleFor(App, Start));
        var replacement = App with { Identity = App.Identity with { CreationFileTime = 21 } };
        Assert.Equal(TimeSpan.Zero, tracker.IdleFor(replacement, Start));
    }
    [Fact] public void Keep_and_profiles_are_respected_even_for_allowed_apps()
    {
        var settings = Settings; var quiet = Quiet(Desktop, App);
        settings.KeepApps.Add(App.AppKey); Assert.False(Assess(App, settings, quiet).Eligible);
        settings.KeepApps.Clear(); settings.Profile = "ทำงาน"; Assert.False(Assess(App, settings, quiet).Eligible);
    }
    [Theory] [InlineData("Codex")] [InlineData("Claude")] [InlineData("ChatGPT")] [InlineData("node")] [InlineData("chrome")] [InlineData("pwsh")]
    public void Work_apps_and_browsers_cannot_be_opted_in(string name)
    {
        var app = App with { Name = name };
        var result = Assess(app, Settings, Quiet(Desktop, app));
        Assert.False(result.CanAllow); Assert.False(result.Eligible);
    }
    [Fact] public void Protects_work_ancestors_descendants_and_every_member_of_a_group()
    {
        var worker = App with { Identity = new(30, 30, @"C:\Tools\node.exe", 1), AppKey = "worker", Name = "node", ParentPid = 20 };
        var child = App with { Identity = new(40, 40, @"C:\Tools\helper.exe", 1), AppKey = "helper", Name = "helper", ParentPid = 30 };
        var otherMember = child with { Identity = new(41, 41, @"C:\Tools\helper.exe", 1), ParentPid = 10 };
        var samples = new[] { Desktop, App, worker, child, otherMember };
        var plan = BoostPolicy.Assess(samples, Settings, Quiet(samples), Start.AddMinutes(5));
        Assert.False(plan.Single(a => a.Group.AppKey == App.AppKey).CanAllow);
        Assert.False(plan.Single(a => a.Group.AppKey == child.AppKey).CanAllow);
        Assert.DoesNotContain(Desktop.Identity.Pid, BoostPolicy.ProtectedWork(samples));
    }
    [Fact] public void Recycled_or_missing_parent_identity_is_not_trusted()
    {
        var orphan = App with { ParentPid = 99 };
        Assert.False(Assess(orphan, Settings, Quiet(Desktop, orphan)).CanAllow);
        var newerDesktop = Desktop with { Identity = Desktop.Identity with { CreationFileTime = 100 } };
        Assert.False(BoostPolicy.Assess([newerDesktop, App], Settings, Quiet(newerDesktop, App), Start.AddMinutes(5)).Single(a => a.Group.AppKey == App.AppKey).CanAllow);
    }
    [Theory] [InlineData(AppRole.WindowsComponent)] [InlineData(AppRole.SharedRuntime)] [InlineData(AppRole.Unknown)] [InlineData(AppRole.Security)]
    public void System_unknown_and_shared_roles_are_always_excluded(AppRole role)
    {
        var app = App with { Role = role };
        Assert.False(Assess(app, Settings, Quiet(Desktop, app)).Eligible);
    }
    [Fact] public void Settings_round_trip_bounds_and_case_insensitivity()
    {
        var settings = StateStore.Import("""{"BoostIdleMinutes":0,"BoostAllowedApps":{"ทำงาน":["FILE:C:\\APPS\\A.EXE"],"ทั่วไป":null}}""");
        Assert.Equal(5, settings.BoostIdleMinutes);
        Assert.Contains("file:c:\\apps\\a.exe", settings.BoostAllowedApps["ทำงาน"]);
        Assert.Empty(settings.BoostAllowedApps["ทั่วไป"]);
        settings.BoostAllowedApps["ทำงาน"] = Enumerable.Range(0, 1001).Select(i => i.ToString()).ToHashSet();
        Assert.Throws<InvalidDataException>(settings.Validate);
    }
    [Fact] public void Restore_journal_survives_reopen_and_history_cleanup()
    {
        string root = Path.Combine(Path.GetTempPath(), "RamMx.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var entry = new BoostEntry(App.Identity, App.Name, App.AppKey, "ทั่วไป", 5, 2);
            using (var store = new StateStore(root)) { store.SaveBoost(entry); store.Save(Settings); store.ClearHistory(); }
            using (var store = new StateStore(root))
            {
                Assert.Equal(entry, Assert.Single(store.PendingBoost()));
                Assert.Contains(App.AppKey, store.Load().BoostAllowedApps["ทั่วไป"]);
                Assert.Throws<InvalidDataException>(() => store.SaveBoost(entry with { OriginalPriority = 99 }));
                store.RemoveBoost(entry.Identity.InstanceKey); Assert.Empty(store.PendingBoost());
            }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (PathPolicy.IsInside(Path.Combine(Path.GetTempPath(), "RamMx.Tests"), root)) Directory.Delete(root, true); }
    }
}
