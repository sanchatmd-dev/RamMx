using RamMx.Core;
using Xunit;

namespace RamMx.Tests;

public sealed class AccessibilityModelTests
{
    [Fact]
    public void Process_row_name_remains_stable_while_metric_cells_update()
    {
        var sample = CoreTests.Sample() with { PrivateWorkingSet = 10UL * 1024 * 1024, CpuPercent = 1 };
        var group = new ProcessGroup { AppKey = sample.AppKey, Name = sample.DisplayName, Members = [sample] };
        var oldName = group.ToString();
        var oldRam = group.RamLabel;
        var oldCpu = group.CpuLabel;
        var changed = new List<string?>();
        group.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        group.UpdateSnapshot(new ProcessGroup
        {
            AppKey = sample.AppKey, Name = sample.DisplayName,
            Members = [sample with { PrivateWorkingSet = 30UL * 1024 * 1024, CpuPercent = 5 }]
        });

        Assert.Equal(sample.DisplayName, oldName);
        Assert.Equal(oldName, group.ToString());
        Assert.NotEqual(oldRam, group.RamLabel);
        Assert.NotEqual(oldCpu, group.CpuLabel);
        Assert.Contains(nameof(ProcessGroup.RamLabel), changed);
        Assert.Contains(nameof(ProcessGroup.CpuLabel), changed);
        Assert.DoesNotContain(nameof(ProcessGroup.Name), changed);
    }
}
