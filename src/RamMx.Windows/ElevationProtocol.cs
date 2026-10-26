using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using RamMx.Core;

namespace RamMx.Windows;

public sealed record BrokerRequest(string Nonce, DateTimeOffset Expires, string Operation, ProcessIdentity[] Targets);
public static class ElevationProtocol
{
    public static async Task<ActionResult> RequestAsync(string operation, IReadOnlyList<ProcessSample> targets, string executable, CancellationToken cancellationToken)
    {
        if (operation is not ("ForceStop" or "DefenderScan")) throw new ArgumentException("Unsupported broker operation");
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var pipeName = "RamMx-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var info = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas" };
        info.ArgumentList.Add("--broker"); info.ArgumentList.Add(pipeName); info.ArgumentList.Add(Environment.ProcessId.ToString()); info.ArgumentList.Add(Process.GetCurrentProcess().StartTime.ToUniversalTime().ToFileTimeUtc().ToString()); info.ArgumentList.Add(nonce);
        using var helper = Process.Start(info) ?? throw new InvalidOperationException("เปิด elevated helper ไม่ได้");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromMinutes(3));
        await server.WaitForConnectionAsync(timeout.Token);
        if (!Native.GetNamedPipeClientProcessId(server.SafePipeHandle, out var peer) || peer != helper.Id) throw new InvalidOperationException("Broker identity ไม่ตรง");
        await WriteAsync(server, new BrokerRequest(nonce, DateTimeOffset.UtcNow.AddMinutes(2), operation, targets.Select(t => t.Identity).ToArray()), timeout.Token);
        return await ReadAsync<ActionResult>(server, timeout.Token);
    }

    public static async Task<(NamedPipeClientStream Pipe, BrokerRequest Request)> ConnectHelperAsync(string[] args, CancellationToken token)
    {
        if (args.Length != 5 || !args[1].StartsWith("RamMx-", StringComparison.Ordinal) || args[1].Length != 38 || !int.TryParse(args[2], out var pid) || !long.TryParse(args[3], out var created) || args[4].Length != 64)
            throw new InvalidOperationException("Broker arguments ไม่ถูกต้อง");
        using var parent = Process.GetProcessById(pid);
        if (parent.SessionId != Process.GetCurrentProcess().SessionId || parent.StartTime.ToUniversalTime().ToFileTimeUtc() != created) throw new InvalidOperationException("Caller session/identity เปลี่ยน");
        using var parentHandle = Native.OpenProcess(0x1000, false, pid);
        var parentPath = Native.ImagePath(parentHandle);
        if (!parentPath.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Caller executable ไม่ตรง");
        var client = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await client.ConnectAsync(10000, token);
            if (!Native.GetNamedPipeServerProcessId(client.SafePipeHandle, out var serverPid) || serverPid != pid) throw new InvalidOperationException("Pipe server ไม่ใช่ caller");
            var request = await ReadAsync<BrokerRequest>(client, token);
            Validate(request, args[4]);
            return (client, request);
        }
        catch { client.Dispose(); throw; }
    }
    public static void Validate(BrokerRequest request, string nonce)
    {
        if (request.Nonce != nonce || request.Expires < DateTimeOffset.UtcNow || request.Expires > DateTimeOffset.UtcNow.AddMinutes(3) || request.Operation is not ("ForceStop" or "DefenderScan") || request.Targets is null || request.Targets.Length is 0 or > 200)
            throw new InvalidOperationException("Broker plan ไม่ผ่านการตรวจ");
        if (request.Operation == "DefenderScan" && request.Targets.Length != 1) throw new InvalidOperationException("สแกนได้ครั้งละหนึ่ง target");
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        var size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is <= 0 or > 65536) throw new InvalidDataException("Broker payload เกินกำหนด");
        var bytes = new byte[size]; await stream.ReadExactlyAsync(bytes, token);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("Broker payload ว่าง");
    }
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > 65536) throw new InvalidDataException("Broker response เกินกำหนด");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(bytes, token); await stream.FlushAsync(token);
    }
}
