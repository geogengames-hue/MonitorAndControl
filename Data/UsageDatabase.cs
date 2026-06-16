using Microsoft.Data.Sqlite;
using MonitorAndControl.Models;

namespace MonitorAndControl.Data;

public class UsageDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private static readonly SemaphoreSlim _lock = new(1, 1);

    public UsageDatabase(string? dbPath = null)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SystemHelper");
            Directory.CreateDirectory(folder);
            dbPath = Path.Combine(folder, "monitor.db");
        }
        else
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(dbPath));
            if (!string.IsNullOrWhiteSpace(folder))
                Directory.CreateDirectory(folder);
        }

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();
        Initialize();
    }

    private void Initialize()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS UsageRecords (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                AppName TEXT NOT NULL,
                ProcessName TEXT NOT NULL,
                Date TEXT NOT NULL,
                TotalSeconds INTEGER NOT NULL DEFAULT 0,
                UNIQUE(AppName, Date)
            );

            CREATE TABLE IF NOT EXISTS AppLimits (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                AppName TEXT NOT NULL UNIQUE,
                DailyMaxMinutes INTEGER NOT NULL DEFAULT 120,
                Enabled INTEGER NOT NULL DEFAULT 1
            );

            CREATE TABLE IF NOT EXISTS ScheduleRules (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                AppName TEXT NOT NULL DEFAULT '',
                DayOfWeek TEXT NOT NULL,
                StartTime TEXT NOT NULL,
                EndTime TEXT NOT NULL,
                Enabled INTEGER NOT NULL DEFAULT 1
            );

            CREATE TABLE IF NOT EXISTS Settings (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS AppMappings (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ProcessName TEXT NOT NULL UNIQUE,
                AppName TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();

        using var migrate = _connection.CreateCommand();
        migrate.CommandText = """
            ALTER TABLE ScheduleRules ADD COLUMN AppName TEXT NOT NULL DEFAULT '';
            """;
        try { migrate.ExecuteNonQuery(); } catch (SqliteException ex) when (ex.SqliteErrorCode == 1) { }
    }

    public async Task RecordUsageAsync(string appName, string processName, int seconds)
    {
        await _lock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO UsageRecords (AppName, ProcessName, Date, TotalSeconds)
                VALUES (@app, @proc, @date, @secs)
                ON CONFLICT(AppName, Date) DO UPDATE SET
                    TotalSeconds = TotalSeconds + @secs,
                    ProcessName = excluded.ProcessName;
                """;
            cmd.Parameters.AddWithValue("@app", appName);
            cmd.Parameters.AddWithValue("@proc", processName);
            cmd.Parameters.AddWithValue("@date", DateTime.Today.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("@secs", seconds);
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _lock.Release(); }
    }

    public async Task<List<AppUsageRecord>> GetTodayUsageAsync()
    {
        return await GetUsageRangeAsync(DateTime.Today, DateTime.Today);
    }

    public async Task<List<AppUsageRecord>> GetUsageRangeAsync(DateTime from, DateTime to)
    {
        await _lock.WaitAsync();
        try
        {
            var list = new List<AppUsageRecord>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT AppName, ProcessName, Date, TotalSeconds
                FROM UsageRecords
                WHERE Date >= @from AND Date <= @to
                ORDER BY TotalSeconds DESC;
                """;
            cmd.Parameters.AddWithValue("@from", from.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("@to", to.ToString("yyyy-MM-dd"));
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new AppUsageRecord
                {
                    AppName = r.GetString(0),
                    ProcessName = r.GetString(1),
                    Date = DateTime.Parse(r.GetString(2)),
                    TotalSeconds = r.GetInt64(3)
                });
            return list;
        }
        finally { _lock.Release(); }
    }

    public async Task ClearUsageHistoryAsync()
    {
        await _lock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM UsageRecords";
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _lock.Release(); }
    }

    public async Task ClearTodayUsageAsync()
    {
        await _lock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM UsageRecords WHERE Date = @date";
            cmd.Parameters.AddWithValue("@date", DateTime.Today.ToString("yyyy-MM-dd"));
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _lock.Release(); }
    }

    public async Task<long> GetAppTodaySecondsAsync(string appName)
    {
        await _lock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT TotalSeconds FROM UsageRecords WHERE AppName = @app AND Date = @date";
            cmd.Parameters.AddWithValue("@app", appName);
            cmd.Parameters.AddWithValue("@date", DateTime.Today.ToString("yyyy-MM-dd"));
            var result = await cmd.ExecuteScalarAsync();
            return result is long l ? l : 0L;
        }
        finally { _lock.Release(); }
    }

    public async Task<List<AppLimitRule>> GetLimitRulesAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var list = new List<AppLimitRule>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT Id, AppName, DailyMaxMinutes, Enabled FROM AppLimits ORDER BY AppName";
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new AppLimitRule
                {
                    Id = r.GetInt32(0),
                    AppName = r.GetString(1),
                    DailyMaxMinutes = r.GetInt32(2),
                    Enabled = r.GetBoolean(3)
                });
            return list;
        }
        finally { _lock.Release(); }
    }

    public async Task SaveLimitRuleAsync(AppLimitRule rule)
    {
        await _lock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO AppLimits (AppName, DailyMaxMinutes, Enabled)
                VALUES (@app, @max, @en)
                ON CONFLICT(AppName) DO UPDATE SET
                    DailyMaxMinutes = @max, Enabled = @en;
                """;
            cmd.Parameters.AddWithValue("@app", rule.AppName);
            cmd.Parameters.AddWithValue("@max", rule.DailyMaxMinutes);
            cmd.Parameters.AddWithValue("@en", rule.Enabled ? 1 : 0);
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteLimitRuleAsync(string appName)
    {
        await _lock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM AppLimits WHERE AppName = @app";
            cmd.Parameters.AddWithValue("@app", appName);
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _lock.Release(); }
    }

    public async Task<List<ScheduleRule>> GetScheduleRulesAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var list = new List<ScheduleRule>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT Id, AppName, DayOfWeek, StartTime, EndTime, Enabled FROM ScheduleRules";
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new ScheduleRule
                {
                    Id = r.GetInt32(0),
                    AppName = r.GetString(1),
                    DayOfWeek = r.GetString(2),
                    StartTime = r.GetString(3),
                    EndTime = r.GetString(4),
                    Enabled = r.GetBoolean(5)
                });
            return list;
        }
        finally { _lock.Release(); }
    }

    public async Task SaveScheduleRuleAsync(ScheduleRule rule)
    {
        await _lock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO ScheduleRules (AppName, DayOfWeek, StartTime, EndTime, Enabled)
                VALUES (@app, @day, @start, @end, @en);
                """;
            cmd.Parameters.AddWithValue("@app", rule.AppName ?? "");
            cmd.Parameters.AddWithValue("@day", rule.DayOfWeek);
            cmd.Parameters.AddWithValue("@start", rule.StartTime);
            cmd.Parameters.AddWithValue("@end", rule.EndTime);
            cmd.Parameters.AddWithValue("@en", rule.Enabled ? 1 : 0);
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _lock.Release(); }
    }

    public async Task UpdateScheduleRuleAsync(ScheduleRule rule)
    {
        await _lock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                UPDATE ScheduleRules SET AppName=@app, DayOfWeek=@day, StartTime=@start, EndTime=@end, Enabled=@en
                WHERE Id=@id;
                """;
            cmd.Parameters.AddWithValue("@id", rule.Id);
            cmd.Parameters.AddWithValue("@app", rule.AppName ?? "");
            cmd.Parameters.AddWithValue("@day", rule.DayOfWeek);
            cmd.Parameters.AddWithValue("@start", rule.StartTime);
            cmd.Parameters.AddWithValue("@end", rule.EndTime);
            cmd.Parameters.AddWithValue("@en", rule.Enabled ? 1 : 0);
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteScheduleRuleAsync(int id)
    {
        await _lock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM ScheduleRules WHERE Id = @id";
            cmd.Parameters.AddWithValue("@id", id);
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _lock.Release(); }
    }

    public async Task<string> GetSettingAsync(string key, string defaultValue = "")
    {
        await _lock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT Value FROM Settings WHERE Key = @key";
            cmd.Parameters.AddWithValue("@key", key);
            var r = await cmd.ExecuteScalarAsync();
            return r?.ToString() ?? defaultValue;
        }
        finally { _lock.Release(); }
    }

    public async Task SetSettingAsync(string key, string value)
    {
        await _lock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "INSERT INTO Settings (Key, Value) VALUES (@k, @v) ON CONFLICT(Key) DO UPDATE SET Value = @v";
            cmd.Parameters.AddWithValue("@k", key);
            cmd.Parameters.AddWithValue("@v", value);
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _lock.Release(); }
    }

    public async Task<int> GetKillDelayAsync()
    {
        var v = await GetSettingAsync("KillDelaySeconds", "30");
        return int.TryParse(v, out var r) ? r : 30;
    }

    public async Task SetKillDelayAsync(int seconds)
    {
        await SetSettingAsync("KillDelaySeconds", seconds.ToString());
    }

    public async Task<bool> GetShowWarningAsync()
    {
        var v = await GetSettingAsync("ShowWarning", "true");
        return bool.TryParse(v, out var r) ? r : true;
    }

    public async Task SetShowWarningAsync(bool show)
    {
        await SetSettingAsync("ShowWarning", show.ToString().ToLower());
    }

    public async Task<string> GetWarningMessageAsync()
    {
        return await GetSettingAsync("WarningMessage", "Time's up! This app will close soon.");
    }

    public async Task SetWarningMessageAsync(string message)
    {
        await SetSettingAsync("WarningMessage", message);
    }

    public async Task<List<AppMapping>> GetAppMappingsAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var list = new List<AppMapping>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT ProcessName, AppName FROM AppMappings";
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new AppMapping(r.GetString(0), r.GetString(1)));
            return list;
        }
        finally { _lock.Release(); }
    }

    public async Task SaveAppMappingAsync(string processName, string appName)
    {
        await _lock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO AppMappings (ProcessName, AppName) VALUES (@p, @a)
                ON CONFLICT(ProcessName) DO UPDATE SET AppName = @a;
                """;
            cmd.Parameters.AddWithValue("@p", processName);
            cmd.Parameters.AddWithValue("@a", appName);
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteAppMappingAsync(string processName)
    {
        await _lock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM AppMappings WHERE ProcessName = @p";
            cmd.Parameters.AddWithValue("@p", processName);
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteAppUsageAsync(string appName)
    {
        await _lock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM UsageRecords WHERE AppName = @app";
            cmd.Parameters.AddWithValue("@app", appName);
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _lock.Release(); }
    }

    public async Task InitializeDefaults(AppConfig config)
    {
        var existing = await GetLimitRulesAsync();
        if (existing.Count == 0 && config.DefaultLimits.Count > 0)
        {
            foreach (var limit in config.DefaultLimits)
                await SaveLimitRuleAsync(limit);
        }
        var schedule = await GetScheduleRulesAsync();
        if (schedule.Count == 0 && config.Schedule.Count > 0)
        {
            foreach (var rule in config.Schedule)
                await SaveScheduleRuleAsync(rule);
        }
    }

    public void Dispose()
    {
        _connection?.Close();
        _connection?.Dispose();
    }
}
