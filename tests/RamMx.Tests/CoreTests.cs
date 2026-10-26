using RamMx.Core;
using RamMx.Persistence;
using RamMx.Windows;
using Xunit;

namespace RamMx.Tests;

public sealed class CoreTests
{
    public static ProcessSample Sample(AppRole role = AppRole.UserApp, bool? critical = false, bool protect = false, bool foreground = false) =>
        new(new(123, 12345, @"C:\Apps\Example\example.exe", 1), "example", "app:example", "Example", role, 200, 100, 300, 1, critical, protect, true, foreground, null, "Example");
    private static ProcessGroup Group(ProcessSample sample) => new() { AppKey = sample.AppKey, Name = sample.DisplayName, Members = [sample] };
    private static MemorySnapshot Memory(DateTimeOffset time, ulong available = 500) => new(time, 1000, available, 500, 2000, 0, 0, 0, 1000, 1000, 500);

    [Theory]
    [InlineData(AppRole.WindowsComponent)] [InlineData(AppRole.Security)] [InlineData(AppRole.ServiceHost)] [InlineData(AppRole.SharedRuntime)] [InlineData(AppRole.Unknown)]
    public void Non_user_roles_cannot_be_stopped(AppRole role) => Assert.False(ActionPolicy.Evaluate(Sample(role)).Allowed);
    [Theory] [InlineData(true)] [InlineData(null)]
    public void Critical_or_unverified_status_is_denied(bool? critical) => Assert.False(ActionPolicy.Evaluate(Sample(critical: critical)).Allowed);
    [Fact] public void Protected_process_is_denied() => Assert.False(ActionPolicy.Evaluate(Sample(protect: true)).Allowed);
    [Fact] public void User_app_with_verified_identity_can_be_stopped() => Assert.True(ActionPolicy.Evaluate(Sample()).Allowed);
    [Fact] public void Missing_creation_time_is_denied() => Assert.False(ActionPolicy.Evaluate(Sample() with { Identity = new(1, 0, @"C:\a.exe", 1) }).Allowed);
    [Fact] public void Private_working_set_is_not_commit() { var group = Group(Sample()); Assert.Equal(100UL, group.PrivateBytes); Assert.Equal(300UL, group.Commit); }
    [Fact] public void Unavailable_private_working_set_is_not_zero() { var group = Group(Sample() with { PrivateWorkingSet = null }); Assert.Equal("อ่านไม่ได้", group.RamLabel); Assert.Equal(0, group.Coverage); }
    [Fact] public void Keep_list_excludes_recommendation()
    {
        var settings = new UserSettings { KeepApps = ["app:example"], ProfileCandidates = new() { ["ทั่วไป"] = ["app:example"] } };
        Assert.Empty(new RecommendationEngine().Evaluate(Memory(DateTimeOffset.UtcNow), [Group(Sample())], settings));
    }
    [Fact] public void Foreground_app_is_never_a_close_recommendation()
    {
        var settings = new UserSettings { ProfileCandidates = new() { ["ทั่วไป"] = ["app:example"] } };
        Assert.Empty(new RecommendationEngine().Evaluate(Memory(DateTimeOffset.UtcNow), [Group(Sample(foreground: true))], settings));
    }
    [Fact] public void Only_explicit_profile_candidates_get_close_recommendations()
    {
        var settings = new UserSettings(); var engine = new RecommendationEngine(); var memory = Memory(DateTimeOffset.UtcNow);
        Assert.Empty(engine.Evaluate(memory, [Group(Sample())], settings));
        settings.ProfileCandidates[settings.Profile] = ["app:example"];
        Assert.Equal("close", Assert.Single(engine.Evaluate(memory, [Group(Sample())], settings)).Action);
    }
    [Fact] public void Pressure_requires_continuous_sixty_seconds()
    {
        var engine = new RecommendationEngine(); var time = DateTimeOffset.UtcNow; var settings = new UserSettings();
        Assert.Empty(engine.Evaluate(Memory(time, 50), [], settings));
        Assert.Empty(engine.Evaluate(Memory(time.AddSeconds(59), 50), [], settings));
        Assert.Single(engine.Evaluate(Memory(time.AddSeconds(60), 50), [], settings));
        Assert.Empty(engine.Evaluate(Memory(time.AddSeconds(61), 500), [], settings)); Assert.False(engine.Pressure);
    }
    [Theory] [InlineData(@"C:\Cache", @"C:\Cache\a.bin", true)] [InlineData(@"C:\Cache", @"C:\Cache2\a.bin", false)] [InlineData(@"C:\Cache", @"C:\Cache\..\secret.txt", false)] [InlineData(@"C:\Cache", @"C:\Cache", false)]
    public void Root_boundary_is_not_a_string_prefix(string root, string path, bool expected) => Assert.Equal(expected, PathPolicy.IsInside(root, path));
    [Fact] public void Import_does_not_enable_startup()
    {
        var settings = StateStore.Import("{\"StartWithWindows\":true,\"RefreshSeconds\":0,\"KeepApps\":[\"app:example\"]}");
        Assert.False(settings.StartWithWindows); Assert.Equal(1, settings.RefreshSeconds); Assert.Contains("app:example", settings.KeepApps);
    }
    [Fact] public void Unknown_settings_schema_is_rejected() => Assert.Throws<InvalidDataException>(() => StateStore.Import("{\"SchemaVersion\":99}"));
    [Fact] public void Quoted_uninstall_path_preserves_arguments()
    {
        var command = UninstallService.ParseCommand("\"C:\\Program Files\\Example\\uninstall.exe\" /uninstall \"example value\"");
        Assert.Equal(@"C:\Program Files\Example\uninstall.exe", command.Executable); Assert.Equal("/uninstall \"example value\"", command.Arguments);
    }
    [Theory] [InlineData("C:\\Program Files\\Example\\uninstall.exe /q")] [InlineData("cmd.exe /c anything")] [InlineData("\"unfinished")] [InlineData("C:\\app.exe\nnext")]
    public void Ambiguous_or_relative_uninstall_commands_are_rejected(string value) => Assert.Throws<ArgumentException>(() => UninstallService.ParseCommand(value));
    [Fact] public void Expired_broker_plan_is_rejected() => Assert.Throws<InvalidOperationException>(() => ElevationProtocol.Validate(new("nonce", DateTimeOffset.UtcNow.AddSeconds(-1), "ForceStop", [Sample().Identity]), "nonce"));
    [Fact] public void Arbitrary_broker_operation_is_rejected() => Assert.Throws<InvalidOperationException>(() => ElevationProtocol.Validate(new("nonce", DateTimeOffset.UtcNow.AddSeconds(30), "RunShell", [Sample().Identity]), "nonce"));
    [Fact] public void Wrong_nonce_is_rejected() => Assert.Throws<InvalidOperationException>(() => ElevationProtocol.Validate(new("nonce", DateTimeOffset.UtcNow.AddSeconds(30), "ForceStop", [Sample().Identity]), "wrong"));
    [Fact] public async Task Oversized_pipe_message_is_rejected() { using var stream = new MemoryStream(BitConverter.GetBytes(65537)); await Assert.ThrowsAsync<InvalidDataException>(() => ElevationProtocol.ReadAsync<BrokerRequest>(stream, default)); }
    [Fact] public void Guid_named_system_package_is_protected_by_path() => Assert.True(InventoryService.IsProtectedInstall("1527c705-839a-4832-9118-54d4bd6a0c89", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SystemApps", "FilePicker")));
    [Fact] public void Windows_prefix_sibling_is_not_an_os_directory() => Assert.False(InventoryService.IsProtectedInstall("Example", Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "-Example"));
    [Fact] public void Shared_runtime_uninstall_is_protected() => Assert.True(InventoryService.IsProtectedInstall("Microsoft Visual C++ 2022 Redistributable", ""));
    [Fact] public void Invalid_thresholds_do_not_disable_pressure_rules() { var settings = new UserSettings { LowAvailablePercent = double.NaN, HighCommitPercent = double.PositiveInfinity }; settings.Validate(); Assert.Equal(10, settings.LowAvailablePercent); Assert.Equal(85, settings.HighCommitPercent); }
}
