using RamMx.Core;
using RamMx.Persistence;
using RamMx.Windows;
using Xunit;

namespace RamMx.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "RamMx.Tests", Guid.NewGuid().ToString("N"));
    public StorageTests() => Directory.CreateDirectory(root);
    [Fact] public void Preferences_survive_restart()
    {
        using (var store = new StateStore(root)) store.Save(new UserSettings { Profile = "ทำงาน", KeepApps = ["file:stable-identity"], RefreshSeconds = 5 });
        using var next = new StateStore(root); Assert.Equal("ทำงาน", next.Load().Profile); Assert.Contains("file:stable-identity", next.Load().KeepApps);
    }
    [Fact] public void Interrupted_action_is_never_replayed()
    {
        using (var store = new StateStore(root)) store.Begin("Uninstall", "fixture");
        using var next = new StateStore(root); Assert.Equal("UnknownOutcome", Assert.Single(next.History()).Status);
    }
    [Fact] public void Completed_action_and_sql_characters_are_stored()
    {
        using var store = new StateStore(root); var id = store.Begin("Close", "app'; DROP TABLE history; --"); store.Complete(id, new(OperationStatus.Completed, "done"));
        Assert.Equal("Completed", Assert.Single(store.History()).Status);
    }
    private (CacheService Service, CacheRecipe Recipe, string File) Cache()
    {
        var recipe = new CacheRecipe("fixture", "Disposable cache", root, ["Cache"], ["no_such_rammx_test_process_8319"]);
        Directory.CreateDirectory(Path.Combine(root, "Cache")); var path = Path.Combine(root, "Cache", "cache.bin"); File.WriteAllBytes(path, new byte[1024]);
        File.WriteAllText(Path.Combine(root, "personal-document.txt"), "preserve me");
        return (new CacheService(recipe), recipe, path);
    }
    [Fact] public void Cache_deletes_only_previewed_unchanged_files()
    {
        var (service, recipe, path) = Cache(); var scan = service.Scan(recipe); var item = Assert.Single(scan.Files);
        Assert.Equal(1024L, item.Size); var result = service.Delete(recipe, scan.Files);
        Assert.Equal(OperationStatus.Completed, result.Status); Assert.False(File.Exists(path)); Assert.True(File.Exists(Path.Combine(root, "personal-document.txt")));
    }
    [Fact] public void Changed_cache_file_is_skipped()
    {
        var (service, recipe, path) = Cache(); var scan = service.Scan(recipe); File.AppendAllText(path, "changed");
        Assert.Equal(OperationStatus.Partial, service.Delete(recipe, scan.Files).Status); Assert.True(File.Exists(path));
    }
    [Fact] public void New_files_after_preview_are_not_deleted()
    {
        var (service, recipe, _) = Cache(); var scan = service.Scan(recipe); var added = Path.Combine(root, "Cache", "new.bin"); File.WriteAllText(added, "new");
        service.Delete(recipe, scan.Files); Assert.True(File.Exists(added));
    }
    [Fact] public void Forged_cleanup_path_is_rejected()
    {
        var (service, recipe, _) = Cache(); var scan = service.Scan(recipe); var forged = scan.Files[0] with { Path = Path.Combine(root, "personal-document.txt") };
        Assert.Equal(OperationStatus.Partial, service.Delete(recipe, [forged]).Status); Assert.True(File.Exists(forged.Path));
    }
    [Fact] public void Locked_cache_file_is_skipped()
    {
        var (service, recipe, path) = Cache(); var scan = service.Scan(recipe); using var held = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(OperationStatus.Partial, service.Delete(recipe, scan.Files).Status);
    }
    [Fact] public void Replaced_same_name_file_is_rejected()
    {
        var (service, recipe, path) = Cache(); var scan = service.Scan(recipe); File.Move(path, path + ".old"); File.WriteAllBytes(path, new byte[1024]);
        Assert.Equal(OperationStatus.Partial, service.Delete(recipe, scan.Files).Status); Assert.True(File.Exists(path));
    }
    public void Dispose()
    {
        // Only this test's randomly-created tree, never an application/user data directory.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root) && PathPolicy.IsInside(Path.Combine(Path.GetTempPath(), "RamMx.Tests"), root)) Directory.Delete(root, true);
    }
}
