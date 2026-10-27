using System.Text.Json;
using Microsoft.Data.Sqlite;
using RamMx.Core;

namespace RamMx.Persistence;

public sealed class StateStore : IDisposable, IBoostJournal
{
    private readonly SqliteConnection connection;
    private readonly object gate = new();
    public string DirectoryPath { get; }
    public StateStore(string? directory = null)
    {
        DirectoryPath = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RamMx");
        Directory.CreateDirectory(DirectoryPath);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(DirectoryPath, "rammx.db"), Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS settings(id INTEGER PRIMARY KEY CHECK(id=1), json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS history(id TEXT PRIMARY KEY, timestamp TEXT NOT NULL, action TEXT NOT NULL, target TEXT NOT NULL, status TEXT NOT NULL, message TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS metrics(timestamp TEXT PRIMARY KEY, available INTEGER, usable INTEGER, committed INTEGER);
            CREATE TABLE IF NOT EXISTS boost_restore(instance_key TEXT PRIMARY KEY, json TEXT NOT NULL);
            PRAGMA user_version=1;
            UPDATE history SET status='UnknownOutcome', message='แอปปิดก่อนบันทึกผล ตรวจสภาพเป้าหมายก่อนทำซ้ำ' WHERE status IN ('Running','Planned');
            """;
        command.ExecuteNonQuery();
        Prune();
    }
    public UserSettings Load()
    {
        lock (gate)
        {
            using var command = connection.CreateCommand(); command.CommandText = "SELECT json FROM settings WHERE id=1";
            var json = command.ExecuteScalar() as string;
            var settings = json == null ? new UserSettings() : JsonSerializer.Deserialize<UserSettings>(json) ?? throw new InvalidDataException("Settings อ่านไม่ได้");
            settings.Validate(); return settings;
        }
    }
    public void Save(UserSettings settings)
    {
        settings.Validate();
        lock (gate)
        {
            using var command = connection.CreateCommand(); command.CommandText = "INSERT INTO settings(id,json) VALUES(1,$json) ON CONFLICT(id) DO UPDATE SET json=$json";
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(settings)); command.ExecuteNonQuery();
        }
    }
    public string Begin(string action, string target)
    {
        if (System.IO.Directory.EnumerateFiles(DirectoryPath).Where(p => Path.GetFileName(p).StartsWith("rammx.db", StringComparison.OrdinalIgnoreCase)).Sum(p => new FileInfo(p).Length) > 50L * 1024 * 1024)
            throw new IOException("พื้นที่ประวัติถึงขีดจำกัด ตรวจ/ส่งออกประวัติก่อนดำเนินการ");
        var id = Guid.NewGuid().ToString("N");
        lock (gate)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO history VALUES($id,$time,$action,$target,'Running','')";
            command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$action", action); command.Parameters.AddWithValue("$target", target); command.ExecuteNonQuery();
        }
        return id;
    }
    public void Complete(string id, ActionResult result)
    {
        lock (gate)
        {
            using var command = connection.CreateCommand(); command.CommandText = "UPDATE history SET status=$status,message=$message WHERE id=$id";
            command.Parameters.AddWithValue("$status", result.Status.ToString()); command.Parameters.AddWithValue("$message", result.Message + (result.Details?.Count > 0 ? "\n" + string.Join("\n", result.Details) : ""));
            command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery();
            Prune();
        }
    }
    public IReadOnlyList<HistoryEntry> History()
    {
        lock (gate)
        {
            using var command = connection.CreateCommand(); command.CommandText = "SELECT * FROM history ORDER BY timestamp DESC LIMIT 1000";
            using var reader = command.ExecuteReader(); var result = new List<HistoryEntry>();
            while (reader.Read()) result.Add(new(reader.GetString(0), DateTimeOffset.Parse(reader.GetString(1)).ToLocalTime(), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
            return result;
        }
    }
    public void Record(MemorySnapshot snapshot)
    {
        lock (gate)
        {
            using var command = connection.CreateCommand(); command.CommandText = "INSERT OR REPLACE INTO metrics VALUES($time,$available,$usable,$commit)";
            command.Parameters.AddWithValue("$time", snapshot.Timestamp.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:00Z"));
            command.Parameters.AddWithValue("$available", (long)snapshot.Available); command.Parameters.AddWithValue("$usable", (long)snapshot.Usable); command.Parameters.AddWithValue("$commit", (long)snapshot.Commit); command.ExecuteNonQuery();
            Prune();
        }
    }
    public void ClearHistory()
    {
        lock (gate)
        {
            using var command = connection.CreateCommand(); command.CommandText = "DELETE FROM history WHERE status NOT IN ('Running','Planned','UnknownOutcome'); DELETE FROM metrics; PRAGMA wal_checkpoint(TRUNCATE);"; command.ExecuteNonQuery();
        }
    }
    public IReadOnlyList<BoostEntry> PendingBoost()
    {
        lock (gate)
        {
            using var command = connection.CreateCommand(); command.CommandText = "SELECT json FROM boost_restore LIMIT 201";
            using var reader = command.ExecuteReader(); var entries = new List<BoostEntry>();
            while (reader.Read())
            {
                var json = reader.GetString(0);
                if (json.Length > 16384 || entries.Count >= 200) throw new InvalidDataException("ข้อมูลคืนค่า Boost เกินขีดจำกัด");
                var entry = JsonSerializer.Deserialize<BoostEntry>(json) ?? throw new InvalidDataException("ข้อมูลคืนค่า Boost ว่าง");
                ValidateBoost(entry); entries.Add(entry);
            }
            return entries;
        }
    }
    public void SaveBoost(BoostEntry entry)
    {
        ValidateBoost(entry);
        lock (gate)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO boost_restore(instance_key,json) VALUES($key,$json) ON CONFLICT(instance_key) DO UPDATE SET json=$json";
            command.Parameters.AddWithValue("$key", entry.Identity.InstanceKey); command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(entry)); command.ExecuteNonQuery();
        }
    }
    private static void ValidateBoost(BoostEntry entry)
    {
        if (entry.Identity == null || entry.Identity.Pid <= 0 || entry.Identity.CreationFileTime <= 0 || string.IsNullOrWhiteSpace(entry.Identity.Path) ||
            entry.Identity.Path.Length > 4096 || !Path.IsPathFullyQualified(entry.Identity.Path) || entry.OriginalPriority is < 3 or > 5 || entry.AppliedPriority != 2)
            throw new InvalidDataException("ข้อมูลคืนค่า Boost ไม่ถูกต้อง");
    }
    public void RemoveBoost(string instanceKey)
    {
        lock (gate)
        {
            using var command = connection.CreateCommand(); command.CommandText = "DELETE FROM boost_restore WHERE instance_key=$key";
            command.Parameters.AddWithValue("$key", instanceKey); command.ExecuteNonQuery();
        }
    }
    private void Prune()
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM metrics WHERE timestamp < $old; DELETE FROM history WHERE status NOT IN ('Running','Planned','UnknownOutcome') AND id NOT IN (SELECT id FROM history ORDER BY timestamp DESC LIMIT 1000); PRAGMA wal_checkpoint(PASSIVE);";
        command.Parameters.AddWithValue("$old", DateTimeOffset.UtcNow.AddDays(-7).ToString("yyyy-MM-ddTHH:mm:ssZ")); command.ExecuteNonQuery();
    }
    public static UserSettings Import(string json)
    {
        if (json.Length > 1024 * 1024) throw new InvalidDataException("ไฟล์ settings ใหญ่เกินกำหนด");
        var value = JsonSerializer.Deserialize<UserSettings>(json) ?? throw new InvalidDataException("Settings ว่าง"); value.Validate();
        value.StartWithWindows = false; return value;
    }
    public void Dispose() => connection.Dispose();
}
