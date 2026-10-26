using RamMx.Core;
using Xunit;

namespace RamMx.Tests;

public sealed class BackgroundReviewTests
{
    private static ProcessGroup Group(ProcessSample sample) => new() { AppKey = sample.AppKey, Name = sample.DisplayName, Members = [sample] };
    [Fact] public void High_ram_alone_does_not_create_a_suspicious_candidate()
    {
        var group = Group(CoreTests.Sample() with { HasWindow = false, PrivateWorkingSet = 2UL * 1024 * 1024 * 1024 });
        Assert.Empty(BackgroundReview.Evaluate([group], new(), DateTimeOffset.Now));
    }
    [Fact] public void Background_temp_process_is_explained_without_malware_verdict()
    {
        var group = Group(CoreTests.Sample(AppRole.Unknown) with { HasWindow = false, Identity = new(12, 123, @"C:\Users\Test\AppData\Local\Temp\example.exe", 1) });
        Assert.Contains("โฟลเดอร์ชั่วคราว", Assert.Single(BackgroundReview.Evaluate([group], new(), DateTimeOffset.Now)).Reason);
    }
    [Fact] public void Foreground_process_is_not_a_background_candidate()
    {
        var group = Group(CoreTests.Sample(AppRole.Unknown, foreground: true) with { HasWindow = false, Identity = new(12, 123, "", 1) });
        Assert.Empty(BackgroundReview.Evaluate([group], new(), DateTimeOffset.Now));
    }
    [Fact] public void Sustained_growth_requires_baseline_and_excludes_system_components()
    {
        var tracker = new BehaviourTracker(); var start = DateTimeOffset.Now;
        var first = Group(CoreTests.Sample() with { HasWindow = false, PrivateWorkingSet = 100UL * 1024 * 1024 }); tracker.Observe(start, [first]);
        Assert.Empty(BackgroundReview.Evaluate([first], tracker, start));
        var last = Group(CoreTests.Sample() with { HasWindow = false, PrivateWorkingSet = 300UL * 1024 * 1024 }); tracker.Observe(start.AddMinutes(5), [last]);
        Assert.Single(BackgroundReview.Evaluate([last], tracker, start.AddMinutes(5)));
        Assert.Empty(BackgroundReview.Evaluate([Group(last.Members[0] with { Role = AppRole.WindowsComponent })], tracker, start.AddMinutes(5)));
    }
}
