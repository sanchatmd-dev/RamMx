using System.Collections.Specialized;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using RamMx.App;
using RamMx.Core;
using Xunit;

namespace RamMx.Tests;

[CollectionDefinition("WPF view models", DisableParallelization = true)]
public sealed class WpfViewModelCollection : ICollectionFixture<StaApplicationFixture> { }

/// <summary>One Application per test process, owned and shut down on its dedicated STA dispatcher.</summary>
public sealed class StaApplicationFixture : IAsyncLifetime
{
    private readonly TaskCompletionSource<Dispatcher> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private System.Windows.Application? application;
    private Thread? thread;

    public async Task InitializeAsync()
    {
        thread = new Thread(() =>
        {
            try
            {
                if (System.Windows.Application.Current != null) throw new InvalidOperationException("The test process already owns a WPF Application.");
                application = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                ready.TrySetResult(dispatcher);
                // Do not start RamMx.App.App: these tests must not create windows, tray icons or user settings.
                Dispatcher.Run();
            }
            catch (Exception error) { ready.TrySetException(error); stopped.TrySetException(error); }
            finally { stopped.TrySetResult(true); }
        }) { IsBackground = true, Name = "RamMx view model tests" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    public async Task RunAsync(Func<Task> test)
    {
        var dispatcher = await ready.Task;
        await dispatcher.InvokeAsync(test).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(45));
    }

    public async Task DisposeAsync()
    {
        if (ready.Task.IsCompletedSuccessfully)
        {
            var dispatcher = await ready.Task;
            if (!dispatcher.HasShutdownStarted)
                await dispatcher.InvokeAsync(() =>
                {
                    application?.Shutdown();
                    // This fixture started Dispatcher.Run directly, so Application does not own its lifetime.
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }).Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (thread != null && !await Task.Run(() => thread.Join(TimeSpan.FromSeconds(5))))
            throw new TimeoutException("The test STA thread did not stop.");
    }
}

[Collection("WPF view models")]
public sealed class ViewModelTests(StaApplicationFixture sta)
{
    [Fact]
    public Task Boost_navigation_preserves_profiles_and_never_enables_a_closed_app() => sta.RunAsync(async () =>
    {
        using var state = new IsolatedViewModel(); var vm = state.ViewModel;
        const string key = @"file:C:\Apps\ClosedFixture\fixture.exe";
        vm.Settings.BoostAllowedApps["ทั่วไป"] = [key];
        await vm.SampleAsync();
        vm.OpenBoostCommand.Execute(null); Assert.Equal(6, vm.Page);
        var row = Assert.Single(vm.BoostRows, r => r.AppKey == key);
        Assert.True(row.Allowed); Assert.False(row.Eligible); Assert.False(vm.BoostCommand.CanExecute(null));
        Assert.False(vm.EndBoostCommand.CanExecute(null)); Assert.False(vm.LowerMemoryPriorityCommand.CanExecute(null));
        row.Keep = true; Assert.Contains(key, vm.Settings.KeepApps);
        vm.Profile = "ทำงาน";
        Assert.Contains(key, vm.Settings.BoostAllowedApps["ทั่วไป"]);
        Assert.DoesNotContain(vm.BoostRows, r => r.AppKey == key && r.Allowed);
        vm.Profile = "ทั่วไป"; row = Assert.Single(vm.BoostRows, r => r.AppKey == key);
        row.Allowed = false; Assert.DoesNotContain(key, vm.Settings.BoostAllowedApps["ทั่วไป"]);
        using var saved = new RamMx.Persistence.StateStore(state.Root);
        Assert.Contains(key, saved.Load().KeepApps);
        Assert.Empty(state.Interaction.Messages);
    });

    [Fact]
    public Task Boost_polling_leaves_other_tables_idle_and_navigation_refreshes_them() => sta.RunAsync(async () =>
    {
        using var state = new IsolatedViewModel(); var vm = state.ViewModel;
        await vm.SampleAsync(); vm.Page = 6; vm.Groups.Clear();
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        await vm.SampleAsync();
        Assert.Empty(vm.Groups); Assert.DoesNotContain(nameof(vm.Chart), notifications);
        Assert.DoesNotContain(nameof(vm.Available), notifications);
        vm.Page = 1;
        Assert.Contains(vm.Groups, g => g.Members.Any(p => p.Identity.Pid == Environment.ProcessId));
    });

    [Fact]
    public void Boost_row_only_notifies_changed_display_cells()
    {
        var sample = BoostTests.App;
        var group = new ProcessGroup { AppKey = sample.AppKey, Name = sample.Name, Members = [sample] };
        var assessment = new BoostAssessment(group, true, false, TimeSpan.Zero, "Waiting");
        var row = new BoostRow(assessment, true, false, (_, _) => { });
        var notifications = new List<string?>(); row.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        row.Update(assessment, true, false); Assert.Empty(notifications);
        var updated = new ProcessGroup { AppKey = group.AppKey, Name = group.Name, Members = [sample with { PrivateWorkingSet = 32 * 1024 * 1024 }] };
        row.Update(assessment with { Group = updated }, true, false);
        Assert.Equal([nameof(BoostRow.Ram)], notifications);
    }

    [Fact]
    public void Boost_labels_are_cached_between_samples_and_refresh_when_ram_becomes_unavailable()
    {
        var sample = BoostTests.App with { PrivateWorkingSet = 1024 };
        var group = new ProcessGroup { AppKey = sample.AppKey, Name = sample.Name, Members = [sample] };
        var row = new BoostRow(new(group, true, false, TimeSpan.Zero, "Waiting"), false, false, (_, _) => { });
        Assert.Equal("1.0 KiB", row.Ram); Assert.Same(row.Ram, row.Ram); Assert.Same(row.Idle, row.Idle);
        group = new ProcessGroup { AppKey = row.AppKey, Name = row.Name, Members = [sample with { PrivateWorkingSet = 64 * 1024 * 1024 }] };
        row.Update(row.Assessment with { Group = group, Idle = TimeSpan.FromMinutes(16) }, true, false);
        Assert.Equal("64.0 MiB", row.Ram); Assert.Equal("16 นาที", row.Idle);
        group = new ProcessGroup { AppKey = row.AppKey, Name = row.Name, Members = [sample with { PrivateWorkingSet = null }] };
        row.Update(row.Assessment with { Group = group }, true, false);
        Assert.Equal("อ่านไม่ได้", row.Ram);
    }

    [Fact]
    public Task Startup_restores_a_recorded_fixture_priority_without_reapplying_boost() => sta.RunAsync(async () =>
    {
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "MemoryFixture", "bin", "Release", "net10.0-windows", "MemoryFixture.exe"));
        using var fixture = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path, "--background")
        { UseShellExecute = false, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden })!;
        try
        {
            await Task.Delay(750);
            var sample = new RamMx.Windows.TelemetryService().ReadProcesses().Single(p => p.Identity.Pid == fixture.Id);
            using var handle = RamMx.Windows.BoostProcessActions.Open(sample.Identity);
            RamMx.Windows.BoostProcessActions.SetPriority(handle, 4);
            uint original = RamMx.Windows.BoostProcessActions.ReadPriority(handle);
            using var state = new IsolatedViewModel();
            // Construct a fresh VM after simulating a crashed session's persisted restore intent.
            using (var journal = new RamMx.Persistence.StateStore(state.Root))
                journal.SaveBoost(new(sample.Identity, sample.Name, sample.AppKey, "ทั่วไป", original, 2));
            RamMx.Windows.BoostProcessActions.SetPriority(handle, 2);
            using var restarted = new MainViewModel(state.Interaction, state.Root);
            await restarted.InitializeAsync();
            Assert.Equal(original, RamMx.Windows.BoostProcessActions.ReadPriority(handle));
            Assert.False(restarted.EndBoostCommand.CanExecute(null));
            Assert.Contains("จบโหมด Boost", restarted.BoostResult);
            Assert.Empty(state.Interaction.Messages);
        }
        finally { if (!fixture.HasExited) { fixture.Kill(); fixture.WaitForExit(3000); } }
    });

    [Fact]
    public Task Inspect_review_opens_evidence_tab_and_keeps_selected_identity() => sta.RunAsync(async () =>
    {
        using var state = new IsolatedViewModel();
        var vm = state.ViewModel;
        await vm.SampleAsync();
        var own = Assert.Single(vm.Groups, g => g.Members.Any(p => p.Identity.Pid == Environment.ProcessId));
        vm.SelectedReview = new(own.AppKey, own.Name, own.RamLabel, "Test-selected read-only target");
        vm.Page = 0; vm.ReviewTab = 0;
        Assert.True(vm.InspectReviewCommand.CanExecute(null));

        vm.InspectReviewCommand.Execute(null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (vm.Busy) await Task.Delay(10, timeout.Token);

        Assert.Equal(4, vm.Page);
        Assert.Equal(1, vm.ReviewTab);
        Assert.Equal(own.AppKey, vm.SelectedGroup?.AppKey);
        Assert.Contains(own.Path, vm.Inspection);
        Assert.NotEmpty(vm.Evidence);
        Assert.Empty(state.Interaction.Messages);
    });

    [Fact]
    public Task Equal_record_batches_do_not_reset_observers_but_changed_data_does() => sta.RunAsync(() =>
    {
        var collection = new BulkObservableCollection<Recommendation>();
        var notifications = new List<NotifyCollectionChangedAction>();
        collection.CollectionChanged += (_, args) => notifications.Add(args.Action);
        var original = new Recommendation("app:fixture", "Review fixture", "Selected by user", "10 MiB", "close");

        collection.ReplaceAll([original]);
        Assert.Equal([NotifyCollectionChangedAction.Reset], notifications);
        notifications.Clear();
        collection.ReplaceAll([original with { }]); // A newly generated but value-equal recommendation.
        Assert.Empty(notifications);
        Assert.Same(original, Assert.Single(collection));

        collection.ReplaceAll([original with { Benefit = "20 MiB" }]);
        Assert.Equal([NotifyCollectionChangedAction.Reset], notifications);
        Assert.Equal("20 MiB", Assert.Single(collection).Benefit);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Hidden_poll_persists_metrics_without_refreshing_visual_collections() => sta.RunAsync(async () =>
    {
        using var state = new IsolatedViewModel();
        var vm = state.ViewModel;
        var changes = new List<string?>();
        vm.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        var collectionChanges = 0;
        vm.Groups.CollectionChanged += (_, _) => collectionChanges++;

        await vm.SampleAsync(updateVisuals: false);

        Assert.NotEqual("—", vm.Available); // Read-only native sampling actually ran.
        Assert.NotEqual("—", vm.Commit);
        Assert.Empty(vm.Groups);
        Assert.Empty(vm.Chart);
        Assert.Equal(0, collectionChanges);
        Assert.Empty(changes);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(state.Root, "rammx.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            connection.Open();
            using var query = connection.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM metrics WHERE usable > 0";
            Assert.Equal(1L, query.ExecuteScalar());
        }

        await vm.SampleAsync();
        Assert.NotEmpty(vm.Groups);
        Assert.NotEmpty(vm.Chart);
        var rows = vm.Groups.ToArray();
        var chart = vm.Chart;
        changes.Clear(); collectionChanges = 0;

        await vm.SampleAsync(updateVisuals: false);

        Assert.Same(chart, vm.Chart);
        Assert.Equal(rows.Length, vm.Groups.Count);
        for (var index = 0; index < rows.Length; index++) Assert.Same(rows[index], vm.Groups[index]);
        Assert.Equal(0, collectionChanges);
        Assert.Empty(changes);
        Assert.Empty(state.Interaction.Messages);
    });

    private sealed class RecordingInteraction : IInteraction
    {
        public List<string> Messages { get; } = [];
        public bool Confirm(string title, string message) => throw new InvalidOperationException("Unexpected confirmation in read-only tests: " + title);
        public void Show(string title, string message) => Messages.Add(title + ": " + message);
        public string? SaveFile(string name) => throw new InvalidOperationException("Unexpected file dialog");
        public string? OpenFile() => throw new InvalidOperationException("Unexpected file dialog");
    }

    private sealed class IsolatedViewModel : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "RamMx.Tests", Guid.NewGuid().ToString("N"));
        public RecordingInteraction Interaction { get; } = new();
        public MainViewModel ViewModel { get; }
        public IsolatedViewModel() => ViewModel = new MainViewModel(Interaction, Root);
        public void Dispose()
        {
            ViewModel.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root) && PathPolicy.IsInside(Path.Combine(Path.GetTempPath(), "RamMx.Tests"), Root))
                Directory.Delete(Root, true);
        }
    }
}
