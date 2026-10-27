using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RamMx.Core;

namespace RamMx.Windows;

public sealed record CacheRecipe(string Id, string Name, string BaseDirectory, string[] RelativeRoots, string[] ProcessNames);
public sealed record CacheFile(string RecipeId, string Root, string Path, long Size, long LastWrite, uint Volume, ulong FileId)
{
    public bool Selected { get; set; } = true;
    public string SizeLabel => Format.Bytes((ulong)Math.Max(0, Size));
    public string Category => "สร้างใหม่ได้ / cache";
}
public sealed record CacheScan(IReadOnlyList<CacheFile> Files, IReadOnlyList<string> Skipped, bool Truncated)
{
    public ulong Bytes => (ulong)Files.Sum(f => f.Size);
}

internal sealed record CacheScanLimits(int MaxFiles = 20000, int MaxEntries = 40000, int MaxDepth = 64, int MaxSkipped = 2000);

public sealed class CacheService
{
    // Only these fixed, application-specific disposable caches are supported. No directory name guessing.
    public IReadOnlyList<CacheRecipe> Recipes { get; }
    private readonly CacheScanLimits limits = new();
    internal CacheService(CacheRecipe fixture, CacheScanLimits? limits = null)
    {
        Recipes = [fixture]; this.limits = limits ?? new();
        if (this.limits.MaxFiles < 1 || this.limits.MaxEntries < 1 || this.limits.MaxDepth < 0 || this.limits.MaxSkipped < 1) throw new ArgumentOutOfRangeException(nameof(limits));
    }
    public CacheService(string? local = null, string? roaming = null)
    {
        local ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        roaming ??= Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Recipes = [
            new("chrome", "Google Chrome · cache ทุก profile", Path.Combine(local, "Google", "Chrome", "User Data"), ["Cache/Cache_Data", "Code Cache", "GPUCache"], ["chrome"]),
            new("edge", "Microsoft Edge · cache ทุก profile", Path.Combine(local, "Microsoft", "Edge", "User Data"), ["Cache/Cache_Data", "Code Cache", "GPUCache"], ["msedge"]),
            new("vscode", "Visual Studio Code · cache", Path.Combine(roaming, "Code"), ["Cache/Cache_Data", "CachedData", "GPUCache", "Code Cache"], ["Code"]),
            new("discord", "Discord · cache", Path.Combine(roaming, "discord"), ["Cache/Cache_Data", "Code Cache", "GPUCache"], ["Discord"]),
            new("slack", "Slack · cache", Path.Combine(roaming, "Slack"), ["Cache/Cache_Data", "Code Cache", "GPUCache"], ["slack"])
        ];
    }

    public bool IsRunning(CacheRecipe recipe)
    {
        var processes = System.Diagnostics.Process.GetProcesses();
        try
        {
            foreach (var process in processes)
            {
                try { if (recipe.ProcessNames.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase)) return true; }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            }
            return false;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public IReadOnlyList<string> ResolveRoots(CacheRecipe recipe) => ResolveRoots(recipe, new ScanBudget(limits), default);

    private IReadOnlyList<string> ResolveRoots(CacheRecipe recipe, ScanBudget budget, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(recipe.BaseDirectory)) return [];
        var bases = new List<string>();
        if (recipe.Id is "chrome" or "edge")
        {
            foreach (var directory in Directory.EnumerateDirectories(recipe.BaseDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!budget.Visit()) break;
                var name = Path.GetFileName(directory);
                if (name is not ("Default" or "Guest Profile") && !name.StartsWith("Profile ", StringComparison.Ordinal)) continue;
                try { if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0) bases.Add(directory); else budget.Skip(directory + ": reparse profile"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception) { budget.Skip(directory + ": " + ex.Message); }
                if (budget.Stop) break;
            }
        }
        else bases.Add(recipe.BaseDirectory);
        return bases.SelectMany(b => recipe.RelativeRoots.Select(r => Path.Combine(b, r.Replace('/', Path.DirectorySeparatorChar)))).Where(Directory.Exists).ToArray();
    }

    public CacheScan Scan(CacheRecipe recipe, CancellationToken cancellationToken = default)
    {
        if (!Recipes.Any(r => r == recipe)) throw new InvalidOperationException("Unknown recipe");
        var files = new List<CacheFile>(); var budget = new ScanBudget(limits);
        foreach (var root in ResolveRoots(recipe, budget, cancellationToken))
        {
            if (budget.Stop) break;
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var locks = LockAncestors(root);
                var pending = new Stack<(string Path, int Depth)>(); pending.Push((root, 0));
                while (pending.Count > 0 && !budget.Stop)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (directory, depth) = pending.Pop();
                    try
                    {
                        // Each queued directory is revalidated under ancestor handles before enumeration.
                        // This keeps traversal iterative without trusting paths queued before a rename/reparse change.
                        using var ancestors = LockAncestors(directory);
                        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (files.Count >= limits.MaxFiles) { budget.Truncate(); break; }
                            if (!budget.Visit()) break;
                            try
                            {
                                var attributes = File.GetAttributes(path);
                                if ((attributes & FileAttributes.ReparsePoint) != 0) { budget.Skip(path + ": reparse point"); continue; }
                                if ((attributes & FileAttributes.Directory) != 0)
                                {
                                    if (depth >= limits.MaxDepth) { budget.Truncated = true; budget.Skip(path + ": maximum scan depth"); }
                                    else pending.Push((path, depth + 1));
                                    continue;
                                }
                                using var file = OpenFile(path, false);
                                var info = Info(file);
                                if ((info.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) { budget.Skip(path + ": changed to reparse point or directory"); continue; }
                                if (info.Links > 1) { budget.Skip(path + ": shared hard link"); continue; }
                                files.Add(new(recipe.Id, root, path, (long)(((ulong)info.SizeHigh << 32) | info.SizeLow), info.WriteTime, info.Volume, ((ulong)info.IndexHigh << 32) | info.IndexLow));
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception) { budget.Skip(path + ": " + ex.Message); }
                            if (budget.Stop) break;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception) { budget.Skip(directory + ": " + ex.Message); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception) { budget.Skip($"{root}: {ex.Message}"); }
        }
        return new(files, budget.Skipped, budget.Truncated);
    }

    private sealed class ScanBudget(CacheScanLimits limits)
    {
        private int visited;
        public List<string> Skipped { get; } = [];
        public bool Truncated { get; set; }
        public bool Stop { get; private set; }
        public bool Visit()
        {
            if (Stop) return false;
            if (visited >= limits.MaxEntries) { Truncate(); return false; }
            visited++; return true;
        }
        public void Skip(string message) { if (Skipped.Count < limits.MaxSkipped) Skipped.Add(message); if (Skipped.Count >= limits.MaxSkipped) Truncate(); }
        public void Truncate() { Truncated = true; Stop = true; }
    }

    public ActionResult Delete(CacheRecipe recipe, IReadOnlyList<CacheFile> selected, CancellationToken cancellationToken = default)
    {
        if (!Recipes.Any(r => r == recipe) || selected.Count > 20000) return new(OperationStatus.Failed, "แผนไม่ถูกต้อง");
        if (IsRunning(recipe)) return new(OperationStatus.Cancelled, "แอปยังเปิดอยู่ โปรดปิดแอปก่อนล้าง cache");
        var roots = ResolveRoots(recipe).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int removed = 0; ulong bytes = 0; var skipped = new List<string>();
        foreach (var item in selected)
        {
            if (cancellationToken.IsCancellationRequested) return new(OperationStatus.Partial, $"ยกเลิกหลังลบ {removed} ไฟล์ ({Format.Bytes(bytes)})", skipped);
            try
            {
                if (item.RecipeId != recipe.Id || !roots.Contains(item.Root) || !PathPolicy.IsInside(item.Root, item.Path)) throw new IOException("อยู่นอกขอบเขต recipe");
                using var ancestors = LockAncestors(Path.GetDirectoryName(item.Path)!);
                using var file = OpenFile(item.Path, true);
                var info = Info(file);
                if ((info.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 || info.Links != 1 || info.Volume != item.Volume || (((ulong)info.IndexHigh << 32) | info.IndexLow) != item.FileId || info.WriteTime != item.LastWrite || (long)(((ulong)info.SizeHigh << 32) | info.SizeLow) != item.Size)
                    throw new IOException("ไฟล์เปลี่ยนหรือถูกใช้ร่วมกันหลัง preview");
                var disposition = new Disposition { Delete = true };
                if (!SetFileInformationByHandle(file, 4, ref disposition, (uint)Marshal.SizeOf<Disposition>())) throw new Win32Exception(Marshal.GetLastWin32Error());
                removed++; bytes += (ulong)item.Size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception) { skipped.Add($"{item.Path}: {ex.Message}"); }
        }
        return new(removed == selected.Count ? OperationStatus.Completed : OperationStatus.Partial,
            $"ลบถาวร {removed}/{selected.Count} ไฟล์ ขนาดไฟล์รวม {Format.Bytes(bytes)} (พื้นที่ว่างจริงอาจต่างจากขนาดไฟล์)", skipped);
    }

    private static SafeFileHandle OpenDirectory(string path)
    {
        var handle = CreateFile(path, 0x80, 3, 0, 3, 0x02200000, 0); // attributes, read/write sharing; deny rename/delete, open reparse itself
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        try
        {
            if ((Info(handle).Attributes & (uint)FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse directory ไม่รองรับ");
            var final = new StringBuilder(32768);
            if (GetFinalPathNameByHandle(handle, final, (uint)final.Capacity, 0) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            var resolved = final.ToString(); if (resolved.StartsWith(@"\\?\")) resolved = resolved[4..];
            if (!Path.TrimEndingDirectorySeparator(resolved).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), StringComparison.OrdinalIgnoreCase)) throw new IOException("Path ถูก redirect");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }
    private static SafeFileHandle OpenFile(string path, bool delete)
    {
        var handle = CreateFile(path, 0x80u | (delete ? 0x10000u : 0u), 1, 0, 3, 0x00200000, 0);
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        return handle;
    }
    private static HandleSet LockAncestors(string path)
    {
        var result = new HandleSet();
        try
        {
            var chain = new Stack<string>(); var current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current)) { chain.Push(current); current = Path.GetDirectoryName(current); }
            while (chain.Count > 0) result.Handles.Add(OpenDirectory(chain.Pop()));
            return result;
        }
        catch { result.Dispose(); throw; }
    }
    private sealed class HandleSet : IDisposable { public List<SafeFileHandle> Handles { get; } = []; public void Dispose() { foreach (var h in Handles.AsEnumerable().Reverse()) h.Dispose(); } }
    [StructLayout(LayoutKind.Sequential, Pack = 4)] private struct FileInformation
    {
        public uint Attributes; public long CreateTime, AccessTime, WriteTime; public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Disposition { [MarshalAs(UnmanagedType.Bool)] public bool Delete; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation info);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref Disposition info, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint size, uint flags);
    private static FileInformation Info(SafeFileHandle handle) => GetFileInformationByHandle(handle, out var info) ? info : throw new Win32Exception(Marshal.GetLastWin32Error());
}
