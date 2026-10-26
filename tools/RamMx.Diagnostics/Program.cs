using System.Diagnostics;
using System.Text.Json;
using RamMx.Windows;

if (args.Length != 2 || !int.TryParse(args[0], out var pid) || !int.TryParse(args[1], out var seconds) || seconds is < 10 or > 86400)
{
    Console.Error.WriteLine("Usage: RamMx.Diagnostics <RamMx PID> <duration seconds, 10..86400>. Read-only; prints aggregate numbers only."); return 2;
}
using var target = Process.GetProcessById(pid);
if (!target.ProcessName.Equals("RamMx", StringComparison.OrdinalIgnoreCase)) { Console.Error.WriteLine("Target must be a RamMx process."); return 2; }
var sampler = new TelemetryService(); var timings = new List<double>(); var privateWorking = new List<ulong>(); var privateCommit = new List<ulong>(); var handles = new List<int>();
var startCpu = target.TotalProcessorTime; var timer = Stopwatch.StartNew(); int processCount = 0;
while (timer.Elapsed.TotalSeconds < seconds)
{
    var round = Stopwatch.StartNew(); var processes = sampler.ReadProcesses(); timings.Add(round.Elapsed.TotalMilliseconds); processCount = processes.Count;
    var own = processes.FirstOrDefault(p => p.Identity.Pid == pid); if (own == null) { Console.Error.WriteLine("RamMx exited during measurement."); return 3; }
    privateWorking.Add(own.PrivateWorkingSet ?? 0); privateCommit.Add(own.PrivateCommit); target.Refresh(); handles.Add(target.HandleCount);
    await Task.Delay(2000);
}
target.Refresh(); timings.Sort();
Console.WriteLine(JsonSerializer.Serialize(new {
    DurationSeconds = timer.Elapsed.TotalSeconds, Samples = timings.Count, ProcessCount = processCount,
    SamplerMedianMs = timings[timings.Count / 2], SamplerP95Ms = timings[Math.Min(timings.Count - 1, (int)Math.Ceiling(timings.Count * .95) - 1)],
    AppAverageCpuPercent = (target.TotalProcessorTime - startCpu).TotalSeconds / timer.Elapsed.TotalSeconds / Environment.ProcessorCount * 100,
    PrivateWorkingSetMaxMiB = privateWorking.Max() / 1048576d, PrivateCommitMaxMiB = privateCommit.Max() / 1048576d,
    PrivateCommitDeltaMiB = ((double)privateCommit.Last() - privateCommit.First()) / 1048576,
    HandlesStart = handles.First(), HandlesEnd = handles.Last(), HandlesMax = handles.Max()
}, new JsonSerializerOptions { WriteIndented = true }));
return 0;
