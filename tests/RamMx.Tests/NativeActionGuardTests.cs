using RamMx.Core;
using RamMx.Windows;
using Xunit;

namespace RamMx.Tests;

public sealed class NativeActionGuardTests
{
    [Fact] public void Unknown_process_protection_denies_stop() => Assert.False(ActionPolicy.Evaluate(CoreTests.Sample() with { Protected = null }).Allowed);
    [Fact] public async Task Cancel_wait_does_not_cancel_native_operation()
    {
        var native = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var wait = UninstallService.WaitForNativeAsync(native.Task, cancel.Token); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait); Assert.False(native.Task.IsCompleted);
        native.SetResult(42); Assert.Equal(42, await native.Task);
    }
    [Fact] public async Task Completed_native_operation_keeps_result() => Assert.Equal(42, await UninstallService.WaitForNativeAsync(Task.FromResult(42), default));
}
