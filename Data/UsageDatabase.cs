using Microsoft.Data.Sqlite;
using MonitorAndControl.Models;

namespace MonitorAndControl.Data;

public class UsageDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly bool _databaseExistedAtStartup;
    private static readonly SemaphoreSlim _lock = new(1, 1);
    public string DatabasePath { get; }

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

        DatabasePath = Path.GetFullPath(dbPath);
        _databaseExistedAtStartup = File.Exists(DatabasePath);
        _connection = new SqliteConnection($"Data Source={DatabasePath}");
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
                ForegroundSeconds INTEGER NOT NULL DEFAULT 0,
                BackgroundSeconds INTEGER NOT NULL DEFAULT 0,
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
                AppName TEXT NOT NULL,
                CountInBackground INTEGER NOT NULL DEFAULT 0,
                IgnoreOverlayFocus INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS AppBonusTime (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                AppName TEXT NOT NULL,
                Date TEXT NOT NULL,
                BonusMinutes INTEGER NOT NULL DEFAULT 0,
                UNIQUE(AppName, Date)
            );

            CREATE TABLE IF NOT EXISTS LimitGroups (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL UNIQUE,
                DailyMaxMinutes INTEGER NOT NULL DEFAULT 180,
                Enabled INTEGER NOT NULL DEFAULT 1
            );

            CREATE TABLE IF NOT EXISTS LimitGroupMembers (
                GroupId INTEGER NOT NULL,
                AppName TEXT NOT NULL UNIQUE,
                PRIMARY KEY (GroupId, AppName),
                FOREIGN KEY (GroupId) REFERENCES LimitGroups(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS LimitGroupUsage (
                GroupId INTEGER NOT NULL,
                Date TEXT NOT NULL,
                TotalSeconds INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (GroupId, Date),
                FOREIGN KEY (GroupId) REFERENCES LimitGroups(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS LimitGroupUsageArchive (
                GroupName TEXT NOT NULL,
                Date TEXT NOT NULL,
                TotalSeconds INTEGER NOT NULL DEFAULT 0,
                UNIQUE(GroupName, Date)
            );

            CREATE TABLE IF NOT EXISTS ProcessedEmailCommands (
                MessageKey TEXT PRIMARY KEY,
                ProcessedAt TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();

        using var migrate = _connection.CreateCommand();
        migrate.CommandText = """
            ALTER TABLE ScheduleRules ADD COLUMN AppName TEXT NOT NULL DEFAULT '';
            """;
        try { migrate.ExecuteNonQuery(); } catch (SqliteException ex) when (ex.SqliteErrorCode == 1) { }

        foreach (var sql in new[]
        {
            "ALTER TABLE AppMappings ADD COLUMN CountInBackground INTEGER NOT NULL DEFAULT 0;",
            "ALTER TABLE AppMappings ADD COLUMN IgnoreOverlayFocus INTEGER NOT NULL DEFAULT 0;"
        })
        {
            using var appMappingMigration = _connection.CreateCommand();
            appMappingMigration.CommandText = sql;
            try { appMappingMigration.ExecuteNonQuery(); }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1) { }
        }

        foreach (var sql in new[]
        {
            "ALTER TABLE UsageRecords ADD COLUMN ForegroundSeconds INTEGER NOT NULL DEFAULT 0;",
            "ALTER TABLE UsageRecords ADD COLUMN BackgroundSeconds INTEGER NOT NULL DEFAULT 0;"
        })
        {
            using var usageMigration = _connection.CreateCommand();
            usageMigration.CommandText = sql;
            try { usageMigration.ExecuteNonQuery(); }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1) { }
        }
    }

    public Task RecordUsageAsync(string appName, string processName, int seconds) =>
        RecordUsageAsync(appName, processName, seconds, 0);

    public async Task RecordUsageAsync(string appName, string processName,
        int foregroundSeconds, int backgroundSeconds)
    {
        if (foregroundSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(foregroundSeconds), "Usage seconds cannot be negative.");
        if (backgroundSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(backgroundSeconds), "Usage seconds cannot be negative.");
        var totalSeconds = foregroundSeconds + backgroundSeconds;
        if (totalSeconds == 0) return;

        await _lock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO UsageRecords
                    (AppName, ProcessName, Date, TotalSeconds, ForegroundSeconds, BackgroundSeconds)
                VALUES (@app, @proc, @date, @total, @foreground, @background)
                ON CONFLICT(AppName, Date) DO UPDATE SET
                    TotalSeconds = TotalSeconds + @total,
                    ForegroundSeconds = ForegroundSeconds + @foreground,
                    BackgroundSeconds = BackgroundSeconds + @background,
                    ProcessName = excluded.ProcessName;
                """;
            cmd.Parameters.AddWithValue("@app", appName);
            cmd.Parameters.AddWithValue("@proc", processName);
            cmd.Parameters.AddWithValue("@date", DateTime.Today.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("@total", totalSeconds);
            cmd.Parameters.AddWithValue("@foreground", foregroundSeconds);
            cmd.Parameters.AddWithValue("@background", backgroundSeconds);
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
                SELECT AppName, ProcessName, Date, TotalSeconds, ForegroundSeconds, BackgroundSeconds
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
                    TotalSeconds = r.GetInt64(3),
                    ForegroundSeconds = r.GetInt64(4),
                    BackgroundSeconds = r.GetInt64(5)
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
            cmd.CommandText = "DELETE FROM UsageRecords; DELETE FROM LimitGroupUsage; DELETE FROM LimitGroupUsageArchive";
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _lock.Release(); }
    }

    public async Task ClearTodayUsageAsync()
    {
        await _lock.WaitAsync();
        try
        {
            using var tx = _connection.BeginTransaction();
            var date = DateTime.Today.ToString("yyyy-MM-dd");

            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM UsageRecords WHERE Date = @date";
                cmd.Parameters.AddWithValue("@date", date);
                await cmd.ExecuteNonQueryAsync();
            }

            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM LimitGroupUsage WHERE Date = @date";
                cmd.Parameters.AddWithValue("@date", date);
                await cmd.ExecuteNonQueryAsync();
            }

            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM LimitGroupUsageArchive WHERE Date = @date";
                cmd.Parameters.AddWithValue("@date", date);
                await cmd.ExecuteNonQueryAsync();
            }

            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM AppBonusTime WHERE Date = @date";
                cmd.Parameters.AddWithValue("@date", date);
                await cmd.ExecuteNonQueryAsync();
            }

            tx.Commit();
        }
        finally { _lock.Release(); }
    }

    public async Task<List<AppBonusTime>> GetTodayBonusTimeAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var list = new List<AppBonusTime>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT AppName, Date, BonusMinutes
                FROM AppBonusTime
                WHERE Date = @date
                ORDER BY AppName;
                """;
            cmd.Parameters.AddWithValue("@date", DateTime.Today.ToString("yyyy-MM-dd"));
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new AppBonusTime
                {
                    AppName = r.GetString(0),
                    Date = r.GetString(1),
                    BonusMinutes = r.GetInt32(2)
                });
            return list;
        }
        finally { _lock.Release(); }
    }

    public async Task<int> GetTodayBonusMinutesAsync(string appName)
    {
        await _lock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT BonusMinutes FROM AppBonusTime WHERE AppName = @app AND Date = @date";
            cmd.Parameters.AddWithValue("@app", appName);
            cmd.Parameters.AddWithValue("@date", DateTime.Today.ToString("yyyy-MM-dd"));
            var result = await cmd.ExecuteScalarAsync();
            return result is long l ? (int)l : 0;
        }
        finally { _lock.Release(); }
    }

    public async Task<int> AddTodayBonusMinutesAsync(string appName, int minutes)
    {
        await _lock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO AppBonusTime (AppName, Date, BonusMinutes)
                VALUES (@app, @date, @minutes)
                ON CONFLICT(AppName, Date) DO UPDATE SET
                    BonusMinutes = BonusMinutes + @minutes;

                SELECT BonusMinutes FROM AppBonusTime WHERE AppName = @app AND Date = @date;
                """;
            cmd.Parameters.AddWithValue("@app", appName);
            cmd.Parameters.AddWithValue("@date", DateTime.Today.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("@minutes", minutes);
            var result = await cmd.ExecuteScalarAsync();
            return result is long l ? (int)l : minutes;
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

    public async Task<Dictionary<string, string>> GetAllSettingsAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT Key, Value FROM Settings ORDER BY Key";
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                settings[r.GetString(0)] = r.GetString(1);
            return settings;
        }
        finally { _lock.Release(); }
    }

    public async Task ReplaceLimitRulesAsync(IEnumerable<AppLimitRule> rules)
    {
        await _lock.WaitAsync();
        try
        {
            using var tx = _connection.BeginTransaction();
            using (var clear = _connection.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = "DELETE FROM AppLimits";
                await clear.ExecuteNonQueryAsync();
            }

            foreach (var rule in rules)
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO AppLimits (AppName, DailyMaxMinutes, Enabled)
                    VALUES (@app, @max, @en);
                    """;
                cmd.Parameters.AddWithValue("@app", rule.AppName);
                cmd.Parameters.AddWithValue("@max", rule.DailyMaxMinutes);
                cmd.Parameters.AddWithValue("@en", rule.Enabled ? 1 : 0);
                await cmd.ExecuteNonQueryAsync();
            }

            tx.Commit();
        }
        finally { _lock.Release(); }
    }

    public async Task ReplaceScheduleRulesAsync(IEnumerable<ScheduleRule> rules)
    {
        await _lock.WaitAsync();
        try
        {
            using var tx = _connection.BeginTransaction();
            using (var clear = _connection.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = "DELETE FROM ScheduleRules";
                await clear.ExecuteNonQueryAsync();
            }

            foreach (var rule in rules)
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
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

            tx.Commit();
        }
        finally { _lock.Release(); }
    }

    public async Task ReplaceAppMappingsAsync(IEnumerable<AppMapping> mappings)
    {
        await _lock.WaitAsync();
        try
        {
            using var tx = _connection.BeginTransaction();
            using (var clear = _connection.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = "DELETE FROM AppMappings";
                await clear.ExecuteNonQueryAsync();
            }

            foreach (var mapping in mappings)
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO AppMappings (ProcessName, AppName, CountInBackground, IgnoreOverlayFocus) VALUES (@p, @a, @background, @overlay)";
                cmd.Parameters.AddWithValue("@p", mapping.ProcessName);
                cmd.Parameters.AddWithValue("@a", mapping.AppName);
                cmd.Parameters.AddWithValue("@background", mapping.CountInBackground ? 1 : 0);
                cmd.Parameters.AddWithValue("@overlay", mapping.IgnoreOverlayFocus ? 1 : 0);
                await cmd.ExecuteNonQueryAsync();
            }

            tx.Commit();
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
            cmd.CommandText = "SELECT ProcessName, AppName, CountInBackground, IgnoreOverlayFocus FROM AppMappings";
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new AppMapping(r.GetString(0), r.GetString(1), r.GetBoolean(2), r.GetBoolean(3)));
            return list;
        }
        finally { _lock.Release(); }
    }

    public async Task SaveAppMappingAsync(string processName, string appName,
        bool countInBackground = false, bool ignoreOverlayFocus = false)
    {
        await _lock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO AppMappings (ProcessName, AppName, CountInBackground, IgnoreOverlayFocus)
                VALUES (@p, @a, @background, @overlay)
                ON CONFLICT(ProcessName) DO UPDATE SET
                    AppName = @a,
                    CountInBackground = @background,
                    IgnoreOverlayFocus = @overlay;
                """;
            cmd.Parameters.AddWithValue("@p", processName);
            cmd.Parameters.AddWithValue("@a", appName);
            cmd.Parameters.AddWithValue("@background", countInBackground ? 1 : 0);
            cmd.Parameters.AddWithValue("@overlay", ignoreOverlayFocus ? 1 : 0);
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

    public async Task<bool> IsEmailCommandProcessedAsync(string messageKey)
    {
        await _lock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM ProcessedEmailCommands WHERE MessageKey = @key LIMIT 1";
            cmd.Parameters.AddWithValue("@key", messageKey);
            return await cmd.ExecuteScalarAsync() != null;
        }
        finally { _lock.Release(); }
    }

    public async Task MarkEmailCommandProcessedAsync(string messageKey, DateTimeOffset? processedAt = null)
    {
        await _lock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO ProcessedEmailCommands (MessageKey, ProcessedAt) VALUES (@key, @at)";
            cmd.Parameters.AddWithValue("@key", messageKey);
            cmd.Parameters.AddWithValue("@at", (processedAt ?? DateTimeOffset.UtcNow).ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteProcessedEmailCommandsBeforeAsync(DateTimeOffset cutoff)
    {
        await _lock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM ProcessedEmailCommands WHERE ProcessedAt < @cutoff";
            cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _lock.Release(); }
    }

    public async Task RecordUsageBatchAsync(
        IEnumerable<(string AppName, string ProcessName, int ForegroundSeconds, int BackgroundSeconds)> appRecords,
        IEnumerable<(int GroupId, int Seconds)> groupRecords)
    {
        await _lock.WaitAsync();
        try
        {
            using var tx = _connection.BeginTransaction();
            var date = DateTime.Today.ToString("yyyy-MM-dd");
            foreach (var record in appRecords)
            {
                var total = record.ForegroundSeconds + record.BackgroundSeconds;
                if (total <= 0) continue;
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO UsageRecords
                        (AppName, ProcessName, Date, TotalSeconds, ForegroundSeconds, BackgroundSeconds)
                    VALUES (@app, @proc, @date, @total, @foreground, @background)
                    ON CONFLICT(AppName, Date) DO UPDATE SET
                        TotalSeconds = TotalSeconds + @total,
                        ForegroundSeconds = ForegroundSeconds + @foreground,
                        BackgroundSeconds = BackgroundSeconds + @background,
                        ProcessName = excluded.ProcessName;
                    """;
                cmd.Parameters.AddWithValue("@app", record.AppName);
                cmd.Parameters.AddWithValue("@proc", record.ProcessName);
                cmd.Parameters.AddWithValue("@date", date);
                cmd.Parameters.AddWithValue("@total", total);
                cmd.Parameters.AddWithValue("@foreground", record.ForegroundSeconds);
                cmd.Parameters.AddWithValue("@background", record.BackgroundSeconds);
                await cmd.ExecuteNonQueryAsync();
            }
            foreach (var record in groupRecords)
            {
                if (record.Seconds <= 0) continue;
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO LimitGroupUsage (GroupId, Date, TotalSeconds)
                    SELECT Id, @date, @seconds FROM LimitGroups WHERE Id = @id
                    ON CONFLICT(GroupId, Date) DO UPDATE SET TotalSeconds = TotalSeconds + @seconds;
                    """;
                cmd.Parameters.AddWithValue("@id", record.GroupId);
                cmd.Parameters.AddWithValue("@date", date);
                cmd.Parameters.AddWithValue("@seconds", record.Seconds);
                await cmd.ExecuteNonQueryAsync();
            }
            tx.Commit();
        }
        finally { _lock.Release(); }
    }

    public async Task<List<AppLimitGroup>> GetLimitGroupsAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var groups = new List<AppLimitGroup>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT g.Id, g.Name, g.DailyMaxMinutes, g.Enabled,
                       COALESCE(u.TotalSeconds, 0)
                FROM LimitGroups g
                LEFT JOIN LimitGroupUsage u ON u.GroupId = g.Id AND u.Date = @date
                ORDER BY g.Name;
                """;
            cmd.Parameters.AddWithValue("@date", DateTime.Today.ToString("yyyy-MM-dd"));
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                groups.Add(new AppLimitGroup
                {
                    Id = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    DailyMaxMinutes = reader.GetInt32(2),
                    Enabled = reader.GetBoolean(3),
                    TodaySeconds = reader.GetInt64(4)
                });

            foreach (var group in groups)
            {
                using var members = _connection.CreateCommand();
                members.CommandText = "SELECT AppName FROM LimitGroupMembers WHERE GroupId = @id ORDER BY AppName";
                members.Parameters.AddWithValue("@id", group.Id);
                using var memberReader = await members.ExecuteReaderAsync();
                while (await memberReader.ReadAsync())
                    group.AppNames.Add(memberReader.GetString(0));
            }
            return groups;
        }
        finally { _lock.Release(); }
    }

    public async Task SaveLimitGroupAsync(AppLimitGroup group)
    {
        await _lock.WaitAsync();
        try
        {
            using var tx = _connection.BeginTransaction();
            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = group.Id > 0
                    ? "UPDATE LimitGroups SET Name=@name, DailyMaxMinutes=@max, Enabled=@enabled WHERE Id=@id"
                    : "INSERT INTO LimitGroups (Name, DailyMaxMinutes, Enabled) VALUES (@name, @max, @enabled) ON CONFLICT(Name) DO UPDATE SET DailyMaxMinutes=@max, Enabled=@enabled";
                cmd.Parameters.AddWithValue("@name", group.Name);
                cmd.Parameters.AddWithValue("@max", group.DailyMaxMinutes);
                cmd.Parameters.AddWithValue("@enabled", group.Enabled ? 1 : 0);
                if (group.Id > 0) cmd.Parameters.AddWithValue("@id", group.Id);
                await cmd.ExecuteNonQueryAsync();
            }

            var groupId = group.Id;
            if (groupId <= 0)
            {
                using var find = _connection.CreateCommand();
                find.Transaction = tx;
                find.CommandText = "SELECT Id FROM LimitGroups WHERE Name=@name";
                find.Parameters.AddWithValue("@name", group.Name);
                groupId = Convert.ToInt32(await find.ExecuteScalarAsync());
            }

            using (var clear = _connection.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = "DELETE FROM LimitGroupMembers WHERE GroupId=@id";
                clear.Parameters.AddWithValue("@id", groupId);
                await clear.ExecuteNonQueryAsync();
            }
            foreach (var appName in group.AppNames.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                using var member = _connection.CreateCommand();
                member.Transaction = tx;
                member.CommandText = "INSERT INTO LimitGroupMembers (GroupId, AppName) VALUES (@id, @app)";
                member.Parameters.AddWithValue("@id", groupId);
                member.Parameters.AddWithValue("@app", appName);
                await member.ExecuteNonQueryAsync();
            }
            tx.Commit();
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteLimitGroupAsync(int id)
    {
        await _lock.WaitAsync();
        try
        {
            using var tx = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO LimitGroupUsageArchive (GroupName, Date, TotalSeconds)
                SELECT g.Name, u.Date, u.TotalSeconds
                FROM LimitGroupUsage u JOIN LimitGroups g ON g.Id = u.GroupId
                WHERE u.GroupId = @id
                ON CONFLICT(GroupName, Date) DO UPDATE SET
                    TotalSeconds = TotalSeconds + excluded.TotalSeconds;
                DELETE FROM LimitGroupMembers WHERE GroupId=@id;
                DELETE FROM LimitGroupUsage WHERE GroupId=@id;
                DELETE FROM LimitGroups WHERE Id=@id;
                """;
            cmd.Parameters.AddWithValue("@id", id);
            await cmd.ExecuteNonQueryAsync();
            tx.Commit();
        }
        finally { _lock.Release(); }
    }

    public async Task RecordLimitGroupUsageAsync(int groupId, int seconds)
    {
        if (seconds <= 0) return;
        await _lock.WaitAsync();
        try
        {
            using var tx = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO LimitGroupUsage (GroupId, Date, TotalSeconds)
                VALUES (@id, @date, @seconds)
                ON CONFLICT(GroupId, Date) DO UPDATE SET TotalSeconds = TotalSeconds + @seconds;
                """;
            cmd.Parameters.AddWithValue("@id", groupId);
            cmd.Parameters.AddWithValue("@date", DateTime.Today.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("@seconds", seconds);
            await cmd.ExecuteNonQueryAsync();
            tx.Commit();
        }
        finally { _lock.Release(); }
    }

    public async Task<List<LimitGroupUsageRecord>> GetLimitGroupUsageRangeAsync(DateTime from, DateTime to)
    {
        await _lock.WaitAsync();
        try
        {
            var records = new List<LimitGroupUsageRecord>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT MAX(GroupId), GroupName, Date, SUM(TotalSeconds)
                FROM (
                    SELECT g.Id AS GroupId, g.Name AS GroupName, u.Date, u.TotalSeconds
                    FROM LimitGroupUsage u JOIN LimitGroups g ON g.Id = u.GroupId
                    UNION ALL
                    SELECT 0 AS GroupId, GroupName, Date, TotalSeconds
                    FROM LimitGroupUsageArchive
                )
                WHERE Date >= @from AND Date <= @to
                GROUP BY GroupName, Date
                ORDER BY Date DESC, SUM(TotalSeconds) DESC;
                """;
            cmd.Parameters.AddWithValue("@from", from.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("@to", to.ToString("yyyy-MM-dd"));
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                records.Add(new LimitGroupUsageRecord
                {
                    GroupId = reader.GetInt32(0),
                    GroupName = reader.GetString(1),
                    Date = DateTime.Parse(reader.GetString(2)),
                    TotalSeconds = reader.GetInt64(3)
                });
            return records;
        }
        finally { _lock.Release(); }
    }

    public async Task ReplaceLimitGroupsAsync(IEnumerable<AppLimitGroup> groups)
    {
        var existing = await GetLimitGroupsAsync();
        foreach (var group in existing)
            await DeleteLimitGroupAsync(group.Id);
        foreach (var group in groups)
        {
            group.Id = 0;
            await SaveLimitGroupAsync(group);
            await RestoreArchivedLimitGroupUsageAsync(group.Name);
        }
    }

    private async Task RestoreArchivedLimitGroupUsageAsync(string groupName)
    {
        await _lock.WaitAsync();
        try
        {
            using var tx = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO LimitGroupUsage (GroupId, Date, TotalSeconds)
                SELECT g.Id, a.Date, a.TotalSeconds
                FROM LimitGroupUsageArchive a JOIN LimitGroups g ON g.Name = a.GroupName COLLATE NOCASE
                WHERE a.GroupName = @name COLLATE NOCASE
                ON CONFLICT(GroupId, Date) DO UPDATE SET
                    TotalSeconds = TotalSeconds + excluded.TotalSeconds;
                DELETE FROM LimitGroupUsageArchive WHERE GroupName = @name COLLATE NOCASE;
                """;
            cmd.Parameters.AddWithValue("@name", groupName);
            await cmd.ExecuteNonQueryAsync();
            tx.Commit();
        }
        finally { _lock.Release(); }
    }

    public async Task RemoveAppFromLimitGroupsAsync(string appName)
    {
        await _lock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM LimitGroupMembers WHERE AppName = @app";
            cmd.Parameters.AddWithValue("@app", appName);
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _lock.Release(); }
    }

    public async Task InitializeDefaults(AppConfig config)
    {
        const string initializedKey = "InitialDefaultsImported";
        if (bool.TryParse(await GetSettingAsync(initializedKey, ""), out var initialized) && initialized)
            return;

        // Existing databases predate the marker. Treat them as already initialized so
        // deliberately empty limit/schedule tables are never repopulated on upgrade.
        if (!_databaseExistedAtStartup)
        {
            foreach (var limit in config.DefaultLimits)
                await SaveLimitRuleAsync(limit);

            foreach (var rule in config.Schedule)
                await SaveScheduleRuleAsync(rule);
        }

        await SetSettingAsync(initializedKey, "true");
    }

    public void Dispose()
    {
        _connection?.Close();
        _connection?.Dispose();
    }
}
