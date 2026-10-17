using Microsoft.Data.Sqlite;

namespace CarsControl.Core.Data;

public sealed record HistoryEntry(
    long Id,
    DateTime Started,
    DateTime? Finished,
    string Kind,          // update | uninstall | cleanup | leftovers
    string TargetKey,
    string Title,
    string Source,
    string? FromVersion,
    string? ToVersion,
    bool Elevated,
    string Status,        // Succeeded | Failed | Rejected | NotFound | Cancelled | DryRun | Skipped
    int ExitCode,
    string? Message,
    string? Output,
    bool Imported = false,
    string? Data = null); // JSON payload, e.g. a quarantine id for restore

public sealed record IgnoreRule(string Key, string? Version, DateTime Created)
{
    public bool IsPermanent => Version is null;
}

/// <summary>%LOCALAPPDATA%\CarsControl\cars.db: history, ignore rules and settings. Per-user, unelevated.</summary>
public sealed class CarsDb
{
    private readonly string _connectionString;

    public CarsDb(string? path = null)
    {
        path ??= Path.Combine(Util.UserData.Root, "cars.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        DatabasePath = path;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        Migrate();
    }

    public string DatabasePath { get; }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private void Migrate()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                started TEXT NOT NULL, finished TEXT,
                kind TEXT NOT NULL, target_key TEXT NOT NULL, title TEXT NOT NULL, source TEXT NOT NULL,
                from_version TEXT, to_version TEXT, elevated INTEGER NOT NULL DEFAULT 0,
                status TEXT NOT NULL, exit_code INTEGER NOT NULL DEFAULT 0,
                message TEXT, output TEXT, imported INTEGER NOT NULL DEFAULT 0, data TEXT);
            CREATE INDEX IF NOT EXISTS ix_history_started ON history(started DESC);
            CREATE TABLE IF NOT EXISTS ignore_rules (key TEXT PRIMARY KEY, version TEXT, created TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT);
            """;
        cmd.ExecuteNonQuery();
    }

    // ---- history ----

    public long AddHistory(HistoryEntry e)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO history (started, finished, kind, target_key, title, source, from_version, to_version, elevated, status, exit_code, message, output, imported, data)
            VALUES ($s, $f, $k, $t, $ti, $src, $fv, $tv, $el, $st, $ec, $m, $o, $imp, $d);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$s", e.Started.ToString("O"));
        cmd.Parameters.AddWithValue("$f", (object?)e.Finished?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$k", e.Kind);
        cmd.Parameters.AddWithValue("$t", e.TargetKey);
        cmd.Parameters.AddWithValue("$ti", e.Title);
        cmd.Parameters.AddWithValue("$src", e.Source);
        cmd.Parameters.AddWithValue("$fv", (object?)e.FromVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tv", (object?)e.ToVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$el", e.Elevated ? 1 : 0);
        cmd.Parameters.AddWithValue("$st", e.Status);
        cmd.Parameters.AddWithValue("$ec", e.ExitCode);
        cmd.Parameters.AddWithValue("$m", (object?)e.Message ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$o", (object?)e.Output ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$imp", e.Imported ? 1 : 0);
        cmd.Parameters.AddWithValue("$d", (object?)e.Data ?? DBNull.Value);
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>Newest first. Output text is left out unless asked for (it can be long; History loads
    /// it per row with <see cref="GetOutput"/>).</summary>
    public IReadOnlyList<HistoryEntry> ListHistory(int limit = 500, bool includeOutput = false, IReadOnlyCollection<string>? targetKeys = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        var where = "";
        if (targetKeys is { Count: > 0 })
        {
            var names = targetKeys.Select((k, i) => $"$k{i}").ToList();
            where = $"WHERE target_key IN ({string.Join(",", names)})";
            var i = 0;
            foreach (var k in targetKeys) cmd.Parameters.AddWithValue($"$k{i++}", k);
        }
        cmd.CommandText = $"SELECT id, started, finished, kind, target_key, title, source, from_version, to_version, elevated, status, exit_code, message, {(includeOutput ? "output" : "NULL")}, imported, data FROM history {where} ORDER BY started DESC LIMIT $l";
        cmd.Parameters.AddWithValue("$l", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<HistoryEntry>();
        while (r.Read())
        {
            list.Add(new HistoryEntry(
                r.GetInt64(0),
                DateTime.Parse(r.GetString(1)),
                r.IsDBNull(2) ? null : DateTime.Parse(r.GetString(2)),
                r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6),
                r.IsDBNull(7) ? null : r.GetString(7),
                r.IsDBNull(8) ? null : r.GetString(8),
                r.GetInt64(9) == 1,
                r.GetString(10),
                (int)r.GetInt64(11),
                r.IsDBNull(12) ? null : r.GetString(12),
                r.IsDBNull(13) ? null : r.GetString(13),
                r.GetInt64(14) == 1,
                r.IsDBNull(15) ? null : r.GetString(15)));
        }
        return list;
    }

    public string? GetOutput(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT output FROM history WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() as string;
    }

    // ---- ignore rules ----

    public IReadOnlyDictionary<string, IgnoreRule> IgnoreRules()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT key, version, created FROM ignore_rules";
        using var r = cmd.ExecuteReader();
        var d = new Dictionary<string, IgnoreRule>(StringComparer.OrdinalIgnoreCase);
        while (r.Read()) d[r.GetString(0)] = new IgnoreRule(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), DateTime.Parse(r.GetString(2)));
        return d;
    }

    /// <summary>version = null ignores every version; otherwise only that version is skipped.</summary>
    public void SetIgnore(string key, string? version)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO ignore_rules (key, version, created) VALUES ($k, $v, $c) ON CONFLICT(key) DO UPDATE SET version = excluded.version, created = excluded.created";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", (object?)version ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$c", DateTime.Now.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void RemoveIgnore(string key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM ignore_rules WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.ExecuteNonQuery();
    }

    public static bool IsIgnored(IReadOnlyDictionary<string, IgnoreRule> rules, string key, string? availableVersion) =>
        rules.TryGetValue(key, out var rule) && (rule.IsPermanent || string.Equals(rule.Version, availableVersion, StringComparison.OrdinalIgnoreCase));

    // ---- settings ----

    public string? GetSetting(string key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string? value)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", (object?)value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}
