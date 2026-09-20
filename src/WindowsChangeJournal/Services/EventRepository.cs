using Microsoft.Data.Sqlite;
using WindowsChangeJournal.Models;

namespace WindowsChangeJournal.Services;

public sealed class EventRepository
{
    private readonly string _connectionString;
    private readonly string _databasePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public EventRepository(string databasePath)
    {
        _databasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        await _gate.WaitAsync();
        try
        {
            await using var connection = await OpenAsync();
            await ExecuteAsync(connection, """
                CREATE TABLE IF NOT EXISTS file_events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    timestamp_utc TEXT NOT NULL,
                    event_type TEXT NOT NULL,
                    path TEXT NOT NULL,
                    old_path TEXT NULL,
                    size INTEGER NULL,
                    source TEXT NULL
                );
                CREATE TABLE IF NOT EXISTS gaps (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    started_utc TEXT NOT NULL,
                    ended_utc TEXT NULL,
                    reason TEXT NOT NULL,
                    scope TEXT NOT NULL,
                    state TEXT NOT NULL DEFAULT 'Unrepaired'
                );
                CREATE TABLE IF NOT EXISTS app_sessions (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    process_name TEXT NOT NULL,
                    process_started_utc TEXT NULL,
                    started_utc TEXT NOT NULL,
                    ended_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                """);

            await AddColumnIfMissingAsync(connection, "file_events", "observed_utc", "TEXT NULL");
            await AddColumnIfMissingAsync(connection, "file_events", "is_directory", "INTEGER NOT NULL DEFAULT 0");
            await AddColumnIfMissingAsync(connection, "file_events", "entity_id", "TEXT NULL");
            await AddColumnIfMissingAsync(connection, "file_events", "confidence", "TEXT NOT NULL DEFAULT 'Observed'");
            await AddColumnIfMissingAsync(connection, "file_events", "foreground_process", "TEXT NULL");
            await AddColumnIfMissingAsync(connection, "file_events", "epoch", "INTEGER NOT NULL DEFAULT 0");
            await AddColumnIfMissingAsync(connection, "file_events", "change_count", "INTEGER NOT NULL DEFAULT 1");
            await ExecuteAsync(connection, """
                UPDATE file_events SET observed_utc = timestamp_utc WHERE observed_utc IS NULL;
                CREATE INDEX IF NOT EXISTS idx_file_events_time ON file_events(timestamp_utc DESC);
                CREATE INDEX IF NOT EXISTS idx_file_events_path ON file_events(path);
                CREATE INDEX IF NOT EXISTS idx_file_events_old_path ON file_events(old_path);
                CREATE INDEX IF NOT EXISTS idx_file_events_type_time ON file_events(event_type, timestamp_utc DESC);
                CREATE INDEX IF NOT EXISTS idx_file_events_entity ON file_events(entity_id, timestamp_utc DESC);
                CREATE INDEX IF NOT EXISTS idx_gaps_time ON gaps(started_utc DESC);
                PRAGMA user_version = 2;
                """);
        }
        finally { _gate.Release(); }
    }

    public Task AddAsync(FileEventRecord item) => AddBatchAsync([item]);

    public async Task AddBatchAsync(IReadOnlyCollection<FileEventRecord> items)
    {
        if (items.Count == 0) return;
        await _gate.WaitAsync();
        try
        {
            await using var connection = await OpenAsync();
            await using var transaction = connection.BeginTransaction();
            foreach (var item in items)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO file_events(
                        timestamp_utc, observed_utc, event_type, path, old_path, size,
                        is_directory, entity_id, source, confidence, foreground_process, epoch, change_count)
                    VALUES($time,$observed,$type,$path,$old,$size,$directory,$entity,$source,$confidence,$foreground,$epoch,$count)
                    """;
                command.Parameters.AddWithValue("$time", item.TimestampUtc.ToString("O"));
                command.Parameters.AddWithValue("$observed", (item.ObservedUtc == default ? DateTimeOffset.UtcNow : item.ObservedUtc).ToString("O"));
                command.Parameters.AddWithValue("$type", item.EventType);
                command.Parameters.AddWithValue("$path", item.Path);
                command.Parameters.AddWithValue("$old", (object?)item.OldPath ?? DBNull.Value);
                command.Parameters.AddWithValue("$size", (object?)item.Size ?? DBNull.Value);
                command.Parameters.AddWithValue("$directory", item.IsDirectory ? 1 : 0);
                command.Parameters.AddWithValue("$entity", (object?)item.EntityId ?? DBNull.Value);
                command.Parameters.AddWithValue("$source", (object?)item.Source ?? DBNull.Value);
                command.Parameters.AddWithValue("$confidence", item.Confidence);
                command.Parameters.AddWithValue("$foreground", (object?)item.ForegroundProcess ?? DBNull.Value);
                command.Parameters.AddWithValue("$epoch", item.Epoch);
                command.Parameters.AddWithValue("$count", Math.Max(1, item.ChangeCount));
                await command.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
        }
        finally { _gate.Release(); }
    }

    public async Task AddGapAsync(DateTimeOffset startedUtc, DateTimeOffset? endedUtc, string reason, string scope)
    {
        await _gate.WaitAsync();
        try
        {
            await using var connection = await OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO gaps(started_utc,ended_utc,reason,scope,state) VALUES($start,$end,$reason,$scope,'Unrepaired')";
            command.Parameters.AddWithValue("$start", startedUtc.ToString("O"));
            command.Parameters.AddWithValue("$end", endedUtc is null ? DBNull.Value : endedUtc.Value.ToString("O"));
            command.Parameters.AddWithValue("$reason", reason);
            command.Parameters.AddWithValue("$scope", scope);
            await command.ExecuteNonQueryAsync();
        }
        finally { _gate.Release(); }
    }

    public async Task AddAppSessionAsync(string processName, DateTimeOffset startedUtc, DateTimeOffset endedUtc, DateTimeOffset? processStartedUtc)
    {
        if (endedUtc <= startedUtc) return;
        await _gate.WaitAsync();
        try
        {
            await using var connection = await OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO app_sessions(process_name,process_started_utc,started_utc,ended_utc) VALUES($name,$processStart,$start,$end)";
            command.Parameters.AddWithValue("$name", processName);
            command.Parameters.AddWithValue("$processStart", processStartedUtc is null ? DBNull.Value : processStartedUtc.Value.ToString("O"));
            command.Parameters.AddWithValue("$start", startedUtc.ToString("O"));
            command.Parameters.AddWithValue("$end", endedUtc.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }
        finally { _gate.Release(); }
    }

    public async Task BeginRecorderSessionAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await using var connection = await OpenAsync();
            var now = DateTimeOffset.UtcNow;
            var active = await GetMetadataAsync(connection, "session_active");
            var previousText = active == "1"
                ? await GetMetadataAsync(connection, "session_heartbeat_utc") ?? await GetMetadataAsync(connection, "session_started_utc")
                : await GetMetadataAsync(connection, "last_shutdown_utc");
            if (DateTimeOffset.TryParse(previousText, out var previous) && now - previous > TimeSpan.FromSeconds(1))
            {
                await using var gap = connection.CreateCommand();
                gap.CommandText = "INSERT INTO gaps(started_utc,ended_utc,reason,scope,state) VALUES($start,$end,$reason,'全部监控范围','Unrepaired')";
                gap.Parameters.AddWithValue("$start", previous.ToString("O"));
                gap.Parameters.AddWithValue("$end", now.ToString("O"));
                gap.Parameters.AddWithValue("$reason", active == "1" ? "上次运行未正常结束或断电" : "程序未运行");
                await gap.ExecuteNonQueryAsync();
            }
            await SetMetadataAsync(connection, "session_active", "1");
            await SetMetadataAsync(connection, "session_started_utc", now.ToString("O"));
            await SetMetadataAsync(connection, "session_heartbeat_utc", now.ToString("O"));
        }
        finally { _gate.Release(); }
    }

    public async Task HeartbeatAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await using var connection = await OpenAsync();
            await SetMetadataAsync(connection, "session_heartbeat_utc", DateTimeOffset.UtcNow.ToString("O"));
        }
        finally { _gate.Release(); }
    }

    public async Task EndRecorderSessionAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await using var connection = await OpenAsync();
            var now = DateTimeOffset.UtcNow.ToString("O");
            await SetMetadataAsync(connection, "session_active", "0");
            await SetMetadataAsync(connection, "session_heartbeat_utc", now);
            await SetMetadataAsync(connection, "last_shutdown_utc", now);
        }
        finally { _gate.Release(); }
    }

    public Task<IReadOnlyList<FileEventRecord>> SearchAsync(string? query, int limit = 500) =>
        SearchAsync(new SearchFilter { Query = query ?? "", Limit = limit });

    public async Task<IReadOnlyList<FileEventRecord>> SearchAsync(SearchFilter filter)
    {
        await _gate.WaitAsync();
        try
        {
            var results = new List<FileEventRecord>();
            await using var connection = await OpenAsync();
            await using var command = connection.CreateCommand();
            var clauses = new List<string>();

            if (!string.IsNullOrWhiteSpace(filter.Query))
            {
                clauses.Add("(path LIKE $pattern ESCAPE '\\' OR COALESCE(old_path,'') LIKE $pattern ESCAPE '\\')");
                var escaped = filter.Query.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
                command.Parameters.AddWithValue("$pattern", $"%{escaped}%");
            }
            if (filter.EventTypes is not null)
            {
                var eventTypes = filter.EventTypes
                    .Where(type => !string.IsNullOrWhiteSpace(type))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (eventTypes.Count == 0) clauses.Add("0 = 1");
                else
                {
                    var parameterNames = new List<string>();
                    for (var index = 0; index < eventTypes.Count; index++)
                    {
                        var parameterName = $"$type{index}";
                        parameterNames.Add(parameterName);
                        command.Parameters.AddWithValue(parameterName, eventTypes[index]);
                    }
                    clauses.Add($"event_type IN ({string.Join(",", parameterNames)})");
                }
            }
            else if (!string.IsNullOrWhiteSpace(filter.EventType))
            {
                clauses.Add("event_type = $type");
                command.Parameters.AddWithValue("$type", filter.EventType);
            }
            if (!string.IsNullOrWhiteSpace(filter.Directory))
            {
                clauses.Add("(path = $directory OR path LIKE $directoryPrefix ESCAPE '\\' OR old_path = $directory OR old_path LIKE $directoryPrefix ESCAPE '\\')");
                var normalized = PathGuard.Normalize(filter.Directory);
                var escaped = normalized.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
                command.Parameters.AddWithValue("$directory", normalized);
                command.Parameters.AddWithValue("$directoryPrefix", escaped.TrimEnd('\\') + "\\\\%");
            }
            if (filter.FromUtc is not null)
            {
                clauses.Add("timestamp_utc >= $from");
                command.Parameters.AddWithValue("$from", filter.FromUtc.Value.ToString("O"));
            }
            if (filter.ToUtc is not null)
            {
                clauses.Add("timestamp_utc <= $to");
                command.Parameters.AddWithValue("$to", filter.ToUtc.Value.ToString("O"));
            }

            var where = clauses.Count == 0 ? "" : "WHERE " + string.Join(" AND ", clauses);
            command.CommandText = $"""
                SELECT id,timestamp_utc,observed_utc,event_type,path,old_path,size,is_directory,
                       entity_id,source,confidence,foreground_process,epoch,change_count
                FROM file_events {where}
                ORDER BY timestamp_utc DESC, id DESC LIMIT $limit
                """;
            command.Parameters.AddWithValue("$limit", Math.Clamp(filter.Limit, 1, 5000));
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) results.Add(ReadEvent(reader));
            return results;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<FileEventRecord>> GetHistoryAsync(FileEventRecord item, int limit = 200)
    {
        await _gate.WaitAsync();
        try
        {
            var results = new List<FileEventRecord>();
            await using var connection = await OpenAsync();
            await using var command = connection.CreateCommand();
            if (!string.IsNullOrWhiteSpace(item.EntityId))
            {
                command.CommandText = "SELECT id,timestamp_utc,observed_utc,event_type,path,old_path,size,is_directory,entity_id,source,confidence,foreground_process,epoch,change_count FROM file_events WHERE entity_id=$entity ORDER BY timestamp_utc DESC,id DESC LIMIT $limit";
                command.Parameters.AddWithValue("$entity", item.EntityId);
            }
            else
            {
                command.CommandText = "SELECT id,timestamp_utc,observed_utc,event_type,path,old_path,size,is_directory,entity_id,source,confidence,foreground_process,epoch,change_count FROM file_events WHERE path=$path OR old_path=$path OR path=$old OR old_path=$old ORDER BY timestamp_utc DESC,id DESC LIMIT $limit";
                command.Parameters.AddWithValue("$path", item.Path);
                command.Parameters.AddWithValue("$old", (object?)item.OldPath ?? item.Path);
            }
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) results.Add(ReadEvent(reader));
            return results;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<GapRecord>> GetRecentGapsAsync(int limit = 50)
    {
        await _gate.WaitAsync();
        try
        {
            var results = new List<GapRecord>();
            await using var connection = await OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id,started_utc,ended_utc,reason,scope,state FROM gaps ORDER BY started_utc DESC LIMIT $limit";
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) results.Add(new GapRecord
            {
                Id = reader.GetInt64(0),
                StartedUtc = DateTimeOffset.Parse(reader.GetString(1)),
                EndedUtc = reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2)),
                Reason = reader.GetString(3), Scope = reader.GetString(4), State = reader.GetString(5)
            });
            return results;
        }
        finally { _gate.Release(); }
    }

    public async Task<RepositoryStats> GetStatsAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await using var connection = await OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*),MIN(timestamp_utc),MAX(timestamp_utc),(SELECT COUNT(*) FROM gaps) FROM file_events";
            await using var reader = await command.ExecuteReaderAsync();
            await reader.ReadAsync();
            return new RepositoryStats
            {
                EventCount = reader.GetInt64(0),
                EarliestUtc = reader.IsDBNull(1) ? null : DateTimeOffset.Parse(reader.GetString(1)),
                LatestUtc = reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2)),
                GapCount = reader.GetInt32(3),
                DatabaseBytes = GetDatabaseBytes()
            };
        }
        finally { _gate.Release(); }
    }

    public async Task RunMaintenanceAsync(int retentionDays, int maxStorageMb)
    {
        await _gate.WaitAsync();
        try
        {
            await using var connection = await OpenAsync();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "DELETE FROM file_events WHERE timestamp_utc < $cutoff; DELETE FROM app_sessions WHERE ended_utc < $cutoff; DELETE FROM gaps WHERE started_utc < $gapCutoff";
                command.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-Math.Clamp(retentionDays, 1, 3650)).ToString("O"));
                command.Parameters.AddWithValue("$gapCutoff", DateTimeOffset.UtcNow.AddDays(-Math.Max(retentionDays, 30)).ToString("O"));
                await command.ExecuteNonQueryAsync();
            }

            var maxBytes = (long)Math.Clamp(maxStorageMb, 128, 4096) * 1024 * 1024;
            var passes = 0;
            while (GetDatabaseBytes() > maxBytes && passes++ < 100)
                await ExecuteAsync(connection, "DELETE FROM file_events WHERE id IN (SELECT id FROM file_events ORDER BY timestamp_utc ASC LIMIT 2000)");
            await ExecuteAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE)");
            if (GetDatabaseBytes() > maxBytes) await ExecuteAsync(connection, "VACUUM");
        }
        finally { _gate.Release(); }
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON; PRAGMA temp_store=MEMORY;");
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AddColumnIfMissingAsync(SqliteConnection connection, string table, string column, string definition)
    {
        await using var check = connection.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await check.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
        await reader.DisposeAsync();
        await ExecuteAsync(connection, $"ALTER TABLE {table} ADD COLUMN {column} {definition}");
    }

    private static async Task<string?> GetMetadataAsync(SqliteConnection connection, string key)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM metadata WHERE key=$key";
        command.Parameters.AddWithValue("$key", key);
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task SetMetadataAsync(SqliteConnection connection, string key, string value)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO metadata(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync();
    }

    private static FileEventRecord ReadEvent(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        TimestampUtc = DateTimeOffset.Parse(reader.GetString(1)),
        ObservedUtc = reader.IsDBNull(2) ? DateTimeOffset.Parse(reader.GetString(1)) : DateTimeOffset.Parse(reader.GetString(2)),
        EventType = reader.GetString(3),
        Path = reader.GetString(4),
        OldPath = reader.IsDBNull(5) ? null : reader.GetString(5),
        Size = reader.IsDBNull(6) ? null : reader.GetInt64(6),
        IsDirectory = reader.GetInt32(7) != 0,
        EntityId = reader.IsDBNull(8) ? null : reader.GetString(8),
        Source = reader.IsDBNull(9) ? null : reader.GetString(9),
        Confidence = reader.IsDBNull(10) ? "Observed" : reader.GetString(10),
        ForegroundProcess = reader.IsDBNull(11) ? null : reader.GetString(11),
        Epoch = reader.GetInt64(12),
        ChangeCount = reader.GetInt32(13)
    };

    private long GetDatabaseBytes()
    {
        static long Length(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
        return Length(_databasePath) + Length(_databasePath + "-wal") + Length(_databasePath + "-shm");
    }
}
