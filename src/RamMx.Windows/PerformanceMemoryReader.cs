using System.Runtime.InteropServices;

namespace RamMx.Windows;

// Reads memory gauges without opening other processes. A query belongs to one poll:
// all four counters share the same collection, and no stale values survive a failure.
internal static class PerformanceMemoryReader
{
    internal sealed record Reading(string Instance, ulong WorkingSet, ulong PrivateWorkingSet, ulong PrivateCommit);
    internal readonly record struct CounterValue(uint Status, long Value);
    private const uint MoreData = 0x800007d2;
    private const uint LargeNoScale = 0x400 | 0x1000;
    private const int MaxBufferBytes = 16 * 1024 * 1024;

    internal static IReadOnlyDictionary<int, Reading> Read()
    {
        if (PdhOpenQuery(null, 0, out var query) != 0) return new Dictionary<int, Reading>();
        try
        {
            // Windows 11 Process V2 uses name:PID instances. Legacy Process array
            // names can repeat without #ordinals and must not be joined by name.
            string[] names = ["Process ID", "Working Set", "Working Set - Private", "Private Bytes"];
            var counters = new nint[names.Length];
            for (int i = 0; i < names.Length; i++)
                if (PdhAddEnglishCounter(query, @"\Process V2(*)\" + names[i], 0, out counters[i]) != 0)
                    return new Dictionary<int, Reading>();
            // These are instantaneous gauges, not rates requiring two samples.
            if (PdhCollectQueryData(query) != 0) return new Dictionary<int, Reading>();
            return Join(ReadArray(counters[0]), ReadArray(counters[1]), ReadArray(counters[2]), ReadArray(counters[3]));
        }
        finally { PdhCloseQuery(query); }
    }

    internal static Dictionary<int, Reading> Join(IReadOnlyDictionary<string, CounterValue> ids,
        IReadOnlyDictionary<string, CounterValue> working, IReadOnlyDictionary<string, CounterValue> privateWorking,
        IReadOnlyDictionary<string, CounterValue> commit)
    {
        var result = new Dictionary<int, Reading>();
        var duplicates = new HashSet<int>();
        var seen = new HashSet<int>();
        foreach (var (instance, id) in ids)
        {
            if (!Valid(id) || id.Value is <= 0 or > int.MaxValue || instance.Equals("_Total", StringComparison.OrdinalIgnoreCase)) continue;
            int pid = (int)id.Value;
            if (!seen.Add(pid)) { duplicates.Add(pid); continue; }
            if (!working.TryGetValue(instance, out var ws) || !Valid(ws) ||
                !privateWorking.TryGetValue(instance, out var pws) || !Valid(pws) ||
                !commit.TryGetValue(instance, out var pc) || !Valid(pc) || pws.Value > ws.Value) continue;
            result.Add(pid, new(instance, (ulong)ws.Value, (ulong)pws.Value, (ulong)pc.Value));
        }
        foreach (var pid in duplicates) result.Remove(pid);
        return result;
    }

    internal static bool Matches(string instance, string processName, int pid)
    {
        int suffix = instance.LastIndexOf(':');
        return suffix > 0 && int.TryParse(instance.AsSpan(suffix + 1), out var instancePid) && instancePid == pid &&
            instance.AsSpan(0, suffix).Equals(processName, StringComparison.OrdinalIgnoreCase);
    }

    internal static void AddValue(Dictionary<string, CounterValue> values, string name, CounterValue value)
    {
        // An unexpected duplicate stays invalid; never silently overwrite another process.
        values[name] = values.ContainsKey(name) ? new(uint.MaxValue, 0) : value;
    }

    private static bool Valid(CounterValue value) => value.Status is 0 or 1 && value.Value >= 0;

    private static Dictionary<string, CounterValue> ReadArray(nint counter)
    {
        var result = new Dictionary<string, CounterValue>(StringComparer.OrdinalIgnoreCase);
        // Retry a growing buffer by querying the size again, as required by PDH.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint size = 0;
            if (PdhGetFormattedCounterArray(counter, LargeNoScale, ref size, out _, 0) != MoreData || size == 0 || size > MaxBufferBytes) return result;
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                uint allocated = size;
                uint status = PdhGetFormattedCounterArray(counter, LargeNoScale, ref size, out uint count, buffer);
                if (status == MoreData) continue;
                int stride = Marshal.SizeOf<CounterItem>();
                if (status != 0 || size > allocated || count > allocated / stride) return result;
                for (int i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<CounterItem>(buffer + i * stride);
                    var name = Marshal.PtrToStringUni(item.Name);
                    if (!string.IsNullOrEmpty(name)) AddValue(result, name, new(item.Value.Status, item.Value.LargeValue));
                }
                return result;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return result;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct FormattedValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public long LargeValue;
    }
    [StructLayout(LayoutKind.Sequential)] private struct CounterItem { public nint Name; public FormattedValue Value; }
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQuery(string? source, nuint userData, out nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounter(nint query, string path, nuint userData, out nint counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint size, out uint count, nint items);
}
