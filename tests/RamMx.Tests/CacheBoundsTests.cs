using System.Runtime.InteropServices;
using RamMx.Core;
using RamMx.Windows;
using Xunit;

namespace RamMx.Tests;

public sealed class CacheBoundsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "RamMx.Tests", Guid.NewGuid().ToString("N"));
    public CacheBoundsTests() => Directory.CreateDirectory(Path.Combine(root, "Cache"));
    private CacheRecipe Recipe(string id = "fixture") => new(id, "Disposable bounds fixture", root, ["Cache"], []);

    [Fact] public void Empty_directories_consume_the_entry_budget()
    {
        for (int i = 0; i < 8; i++) Directory.CreateDirectory(Path.Combine(root, "Cache", "empty" + i));
        var recipe = Recipe(); var result = new CacheService(recipe, new(MaxEntries: 3)).Scan(recipe);
        Assert.True(result.Truncated); Assert.Empty(result.Files);
        Assert.Equal(8, Directory.GetDirectories(Path.Combine(root, "Cache")).Length);
    }

    [Fact] public void Deep_directory_is_skipped_without_recursive_traversal()
    {
        var deep = Path.Combine(root, "Cache", "one", "two", "three"); Directory.CreateDirectory(deep);
        var personal = Path.Combine(deep, "preserve.bin"); File.WriteAllText(personal, "untouched");
        var recipe = Recipe(); var result = new CacheService(recipe, new(MaxDepth: 1)).Scan(recipe);
        Assert.True(result.Truncated); Assert.Empty(result.Files); Assert.Contains(result.Skipped, s => s.Contains("maximum scan depth"));
        Assert.Equal("untouched", File.ReadAllText(personal));
    }

    [Fact] public void Hard_link_skip_messages_are_bounded()
    {
        var personal = Path.Combine(root, "personal.bin"); File.WriteAllText(personal, "untouched");
        for (int i = 0; i < 6; i++) Assert.True(CreateHardLink(Path.Combine(root, "Cache", "link" + i + ".bin"), personal, 0));
        var recipe = Recipe(); var result = new CacheService(recipe, new(MaxSkipped: 2)).Scan(recipe);
        Assert.True(result.Truncated); Assert.Empty(result.Files); Assert.Equal(2, result.Skipped.Count);
        Assert.All(result.Skipped, s => Assert.Contains("hard link", s)); Assert.Equal("untouched", File.ReadAllText(personal));
    }

    [Fact] public void Accepted_files_have_their_own_limit()
    {
        for (int i = 0; i < 4; i++) File.WriteAllText(Path.Combine(root, "Cache", i + ".bin"), "cache");
        var recipe = Recipe(); var result = new CacheService(recipe, new(MaxFiles: 2)).Scan(recipe);
        Assert.True(result.Truncated); Assert.Equal(2, result.Files.Count); Assert.Equal(4, Directory.GetFiles(Path.Combine(root, "Cache")).Length);
    }

    [Fact] public void Browser_profile_discovery_is_also_bounded()
    {
        for (int i = 0; i < 8; i++) Directory.CreateDirectory(Path.Combine(root, "Profile " + i, "Cache"));
        var recipe = Recipe("chrome"); var result = new CacheService(recipe, new(MaxEntries: 3)).Scan(recipe);
        Assert.True(result.Truncated); Assert.Empty(result.Files);
    }

    [Fact] public void Scan_cancellation_never_mutates_fixture_files()
    {
        var path = Path.Combine(root, "Cache", "cache.bin"); File.WriteAllText(path, "untouched");
        var recipe = Recipe(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new CacheService(recipe).Scan(recipe, cancellation.Token));
        Assert.Equal("untouched", File.ReadAllText(path));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLink(string path, string existing, nint security);

    public void Dispose()
    {
        if (Directory.Exists(root) && PathPolicy.IsInside(Path.Combine(Path.GetTempPath(), "RamMx.Tests"), root)) Directory.Delete(root, true);
    }
}
