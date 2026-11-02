using Microsoft.Data.Sqlite;
using MonitorAndControl.Models;
using MonitorAndControl.Services;

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
            // SYSTEM-protected data directory (see AppPaths) - the watchdog denies
            // Delete on monitor.db so a standard user cannot wipe usage history.
            dbPath = AppPaths.DatabasePath;
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

            CREATE TABLE IF NOT EXISTS AppUserLimits (
                AppName TEXT NOT NULL COLLATE NOCASE,
                UserSid TEXT NOT NULL,
                DailyMaxMinutes INTEGER NOT NULL DEFAULT 120,
                Enabled INTEGER NOT NULL DEFAULT 1,
                PRIMARY KEY (AppName, UserSid)
            );

            CREATE TABLE IF NOT EXISTS UserAppUsage (
                UserSid TEXT NOT NULL,
                AppName TEXT NOT NULL COLLATE NOCASE,
                Date TEXT NOT NULL,
                Seconds INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (UserSid, AppName, Date)
            );

            CREATE TABLE IF NOT EXISTS ActivityHours (
                Hour TEXT NOT NULL,
                Kind TEXT NOT NULL,
                Name TEXT NOT NULL,
                Seconds INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (Hour, Kind, Name)
            );

            DELETE FROM ActivityHours WHERE Hour < strftime('%Y-%m-%d %H', 'now', 'localtime', '-90 days');

            CREATE TABLE IF NOT EXISTS SiteLimits (
                Host TEXT PRIMARY KEY,
                DailyMaxMinutes INTEGER NOT NULL DEFAULT 0,
                Enabled INTEGER NOT NULL DEFAULT 1
            );

            CREATE TABLE IF NOT EXISTS SiteRequests (
                Id TEXT PRIMARY KEY,
                UserSid TEXT NOT NULL,
                Account TEXT NOT NULL DEFAULT '',
                Host TEXT NOT NULL,
                CreatedUtc TEXT NOT NULL,
                State TEXT NOT NULL DEFAULT 'waiting',
                Alerted INTEGER NOT NULL DEFAULT 0,
                Told INTEGER NOT NULL DEFAULT 1
            );

            CREATE TABLE IF NOT EXISTS QuickActions (
                Hash TEXT PRIMARY KEY,
                UserSid TEXT NOT NULL,
                RequestId TEXT NOT NULL,
                ExpiresUtc TEXT NOT NULL,
                Used INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS UserSiteLimits (
                UserSid TEXT NOT NULL,
                Host TEXT NOT NULL,
                DailyMaxMinutes INTEGER NOT NULL DEFAULT 0,
                Enabled INTEGER NOT NULL DEFAULT 1,
                PRIMARY KEY (UserSid, Host)
            );

            CREATE TABLE IF NOT EXISTS UserSiteUsage (
                UserSid TEXT NOT NULL,
                Host TEXT NOT NULL,
                Date TEXT NOT NULL,
                Seconds INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (UserSid, Host, Date)
            );

            CREATE TABLE IF NOT EXISTS SiteUsage (
                Host TEXT NOT NULL,
                Date TEXT NOT NULL,
                Seconds INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (Host, Date)
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
                AppName TEXT NOT NULL,
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
            "ALTER TABLE UsageRecords ADD COLUMN BackgroundSeconds INTEGER NOT NULL DEFAULT 0;",
            "ALTER TABLE LimitGroups ADD COLUMN UserSid TEXT NOT NULL DEFAULT '';",
            "ALTER TABLE ScheduleRules ADD COLUMN UserSid TEXT NOT NULL DEFAULT '';"
        })
        {
            using var usageMigration = _connection.CreateCommand();
            usageMigration.CommandText = sql;
            try { usageMigration.ExecuteNonQuery(); }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1) { }
        }

        // Older databases allowed an app in only one group overall; groups for different accounts may now share apps.
        using (var check = _connection.CreateCommand())
        {
            check.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'LimitGroupMembers'";
            if ((check.ExecuteScalar() as string)?.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true)
            {
                using var tx = _connection.BeginTransaction();
                using var rebuild = _connection.CreateCommand();
                rebuild.Transaction = tx;
                rebuild.CommandText = """
                    CREATE TABLE LimitGroupMembers_new (
                        GroupId INTEGER NOT NULL,
                        AppName TEXT NOT NULL,
                        PRIMARY KEY (GroupId, AppName),
                        FOREIGN KEY (GroupId) REFERENCES LimitGroups(Id) ON DELETE CASCADE
                    );
                    INSERT INTO LimitGroupMembers_new (GroupId, AppName) SELECT GroupId, AppName FROM LimitGroupMembers;
                    DROP TABLE LimitGroupMembers;
                    ALTER TABLE LimitGroupMembers_new RENAME TO LimitGroupMembers;
                    """;
                rebuild.ExecuteNonQuery();
                tx.Commit();
            }
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
            cmd.Parameters.AddWithValue("@date", MonitorAndControl.ScreenTime.ParentClock.Today.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("@total", totalSeconds);
            cmd.Parameters.AddWithValue("@foreground", foregroundSeconds);
            cmd.Parameters.AddWithValue("@background", backgroundSeconds);
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _lock.Release(); }
    }

    public async Task<List<AppUsageRecord>> GetTodayUsageAsync()
    {
        return await GetUsageRangeAsync(MonitorAndControl.ScreenTime.ParentClock.Today, MonitorAndControl.ScreenTime.ParentClock.Today);
    }

    // ---- Limits for one Windows account ------------------------------------------------------
    public async Task<List<AppLimitRule>> GetUserLimitRulesAsync(string? userSid = null)
    {
        await _lock.WaitAsync();
        try
        {
            var list = new List<AppLimitRule>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = userSid == null
                ? "SELECT AppName, UserSid, DailyMaxMinutes, Enabled FROM AppUserLimits ORDER BY UserSid, AppName"
                : "SELECT AppName, UserSid, DailyMaxMinutes, Enabled FROM AppUserLimits WHERE UserSid = @sid ORDER BY AppName";
            if (userSid != null) cmd.Parameters.AddWithValue("@sid", userSid);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new AppLimitRule { AppName = r.GetString(0), UserSid = r.GetString(1), DailyMaxMinutes = r.GetInt32(2), Enabled = r.GetBoolean(3) });
            return list;
        }
        finally { _lock.Release(); }
    }

    public async Task SaveUserLimitRuleAsync(AppLimitRule rule)
    {
        await _lock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO AppUserLimits (AppName, UserSid, DailyMaxMinutes, Enabled) VALUES (@app, @sid, @max, @en)
                ON CONFLICT(AppName, UserSid) DO UPDATE SET DailyMaxMinutes = @max, Enabled = @en;
                """;
            cmd.Parameters.AddWithValue("@app", rule.AppName);
            cmd.Parameters.AddWithValue("@sid", rule.UserSid);
            cmd.Parameters.AddWithValue("@max", rule.DailyMaxMinutes);
            cmd.Parameters.AddWithValue("@en", rule.Enabled ? 1 : 0);
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteUserLimitRuleAsync(string appName, string userSid)
    {
        await _lock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM AppUserLimits WHERE AppName = @app AND UserSid = @sid";
            cmd.Parameters.AddWithValue("@app", appName);
            cmd.Parameters.AddWithValue("@sid", userSid);
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _lock.Release(); }
    }

    public async Task ReplaceUserLimitRulesAsync(IEnumerable<AppLimitRule> rules)
    {
        await _lock.WaitAsync();
        try
        {
            using var tx = _connection.BeginTransaction();
            using (var clear = _connection.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = "DELETE FROM AppUserLimits";
                await clear.ExecuteNonQueryAsync();
            }
            foreach (var rule in rules.Where(r => !string.IsNullOrEmpty(r.UserSid)))
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR REPLACE INTO AppUserLimits (AppName, UserSid, DailyMaxMinutes, Enabled) VALUES (@app, @sid, @max, @en)";
                cmd.Parameters.AddWithValue("@app", rule.AppName);
                cmd.Parameters.AddWithValue("@sid", rule.UserSid);
                cmd.Parameters.AddWithValue("@max", rule.DailyMaxMinutes);
                cmd.Parameters.AddWithValue("@en", rule.Enabled ? 1 : 0);
                await cmd.ExecuteNonQueryAsync();
            }
            tx.Commit();
        }
        finally { _lock.Release(); }
    }

    /// <summary>Today's seconds per app for one Windows account.</summary>
    public async Task<Dictionary<string, long>> GetUserAppUsageTodayAsync(string userSid)
    {
        await _lock.WaitAsync();
        try
        {
            var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT AppName, Seconds FROM UserAppUsage WHERE UserSid = @sid AND Date = @date";
            cmd.Parameters.AddWithValue("@sid", userSid);
            cmd.Parameters.AddWithValue("@date", MonitorAndControl.ScreenTime.ParentClock.Today.ToString("yyyy-MM-dd"));
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) map[r.GetString(0)] = map.GetValueOrDefault(r.GetString(0)) + r.GetInt64(1);
            return map;
        }
        finally { _lock.Release(); }
    }

    // ---- Single-use "Not now" links from phone alerts ------------------------------------------
    // ---- "Ask to open this site" requests --------------------------------------------------
    public const int MaxWaitingSiteRequests = 50;

    /// <summary>Records a request unless this account already has one waiting for the site. Returns false for a duplicate.</summary>
    public async Task<bool> AddSiteRequestAsync(SiteRequest request)
    {
        await _lock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                DELETE FROM SiteRequests WHERE CreatedUtc < @old;
                INSERT INTO SiteRequests (Id, UserSid, Account, Host, CreatedUtc, State, Alerted, Told)
                SELECT @id, @sid, @account, @host, @now, 'waiting', 0, 1
                WHERE NOT EXISTS (SELECT 1 FROM SiteRequests WHERE UserSid = @sid AND Host = @host AND State = 'waiting' AND CreatedUtc >= @since)
                  AND (SELECT COUNT(*) FROM SiteRequests WHERE State = 'waiting' AND CreatedUtc >= @since) < @max;
                """;
            cmd.Parameters.AddWithValue("@old", DateTimeOffset.UtcNow.AddDays(-7).ToString("O"));
            // Requests older than a day have lapsed (the parent no longer sees them), so they do not count.
            cmd.Parameters.AddWithValue("@since", DateTimeOffset.UtcNow.Add(-SiteRequest.Lifetime).ToString("O"));
            cmd.Parameters.AddWithValue("@id", request.Id);
            cmd.Parameters.AddWithValue("@sid", request.UserSid);
            cmd.Parameters.AddWithValue("@account", request.Account);
            cmd.Parameters.AddWithValue("@host", request.Host);
            cmd.Parameters.AddWithValue("@now", request.CreatedUtc.ToUniversalTime().ToString("O"));
            cmd.Parameters.AddWithValue("@max", MaxWaitingSiteRequests);
            return await cmd.ExecuteNonQueryAsync() > 0;
        }
        finally { _lock.Release(); }
    }

    /// <summary>Requests still waiting (null = every account); requests older than a day lapse.</summary>
    public async Task<List<SiteRequest>> GetSiteRequestsAsync(string? state = "waiting", string? userSid = null)
    {
        await _lock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT Id, UserSid, Account, Host, CreatedUtc, State, Alerted, Told FROM SiteRequests " +
                              "WHERE (@state IS NULL OR State = @state) AND (@sid IS NULL OR UserSid = @sid) AND CreatedUtc >= @since ORDER BY CreatedUtc";
            cmd.Parameters.AddWithValue("@state", (object?)state ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@sid", (object?)userSid ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@since", DateTimeOffset.UtcNow.Add(-SiteRequest.Lifetime).ToString("O"));
            using var r = await cmd.ExecuteReaderAsync();
            var list = new List<SiteRequest>();
            while (await r.ReadAsync())
                list.Add(new SiteRequest
                {
                    Id = r.GetString(0), UserSid = r.GetString(1), Account = r.GetString(2), Host = r.GetString(3),
                    CreatedUtc = DateTimeOffset.Parse(r.GetString(4), System.Globalization.CultureInfo.InvariantCulture),
                    State = r.GetString(5), Alerted = r.GetInt64(6) != 0, Told = r.GetInt64(7) != 0
                });
            return list;
        }
        finally { _lock.Release(); }
    }

    /// <summary>Claims a waiting request for alerting, so two accounts' DeviceMon do not both send it. False if already claimed.</summary>
    public Task<bool> ClaimSiteRequestAlertAsync(string id, bool claim) =>
        ExecuteAsync(claim ? "UPDATE SiteRequests SET Alerted = 1 WHERE Id = @id AND Alerted = 0"
                           : "UPDATE SiteRequests SET Alerted = 0 WHERE Id = @id", ("@id", id));

    /// <summary>Answers one request ("allowed" or "declined"); the child is told by their own DeviceMon.</summary>
    public Task<bool> AnswerSiteRequestAsync(string id, bool allowed) =>
        ExecuteAsync("UPDATE SiteRequests SET State = @state, Told = 0 WHERE Id = @id AND State = 'waiting'",
            ("@id", id), ("@state", allowed ? "allowed" : "declined"));

    public Task<bool> MarkSiteRequestToldAsync(string id) =>
        ExecuteAsync("UPDATE SiteRequests SET Told = 1 WHERE Id = @id", ("@id", id));

    private async Task<bool> ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await _lock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
            return await cmd.ExecuteNonQueryAsync() > 0;
        }
        finally { _lock.Release(); }
    }

    public async Task AddQuickActionAsync(string hash, string userSid, string requestId, DateTimeOffset expiresUtc)
    {
        await _lock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                DELETE FROM QuickActions WHERE ExpiresUtc < @now;
                INSERT OR REPLACE INTO QuickActions (Hash, UserSid, RequestId, ExpiresUtc, Used) VALUES (@hash, @sid, @req, @exp, 0);
                """;
            cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@hash", hash);
            cmd.Parameters.AddWithValue("@sid", userSid);
            cmd.Parameters.AddWithValue("@req", requestId);
            cmd.Parameters.AddWithValue("@exp", expiresUtc.ToUniversalTime().ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _lock.Release(); }
    }

    /// <summary>The account and request a link is for, or null when it is unknown, expired or already used.</summary>
    public async Task<(string UserSid, string RequestId)?> FindQuickActionAsync(string hash, DateTimeOffset now)
    {
        await _lock.WaitAsync();
        try
        {
            using var find = _connection.CreateCommand();
            find.CommandText = "SELECT UserSid, RequestId FROM QuickActions WHERE Hash = @hash AND Used = 0 AND ExpiresUtc > @now";
            find.Parameters.AddWithValue("@hash", hash);
            find.Parameters.AddWithValue("@now", now.ToUniversalTime().ToString("O"));
            using var r = await find.ExecuteReaderAsync();
            return await r.ReadAsync() ? (r.GetString(0), r.GetString(1)) : null;
        }
        finally { _lock.Release(); }
    }

    /// <summary>Uses up a link once its action has been carried out.</summary>
    public async Task MarkQuickActionUsedAsync(string hash)
    {
        await _lock.WaitAsync();
        try
        {
            using var use = _connection.CreateCommand();
            use.CommandText = "UPDATE QuickActions SET Used = 1 WHERE Hash = @hash";
            use.Parameters.AddWithValue("@hash", hash);
            await use.ExecuteNonQueryAsync();
        }
        finally { _lock.Release(); }
    }

    // ---- Website rules for one Windows account ----------------------------------------------
    public async Task<List<SiteLimit>> GetUserSiteLimitsAsync(string? userSid = null)
    {
        await _lock.WaitAsync();
        try
        {
            var list = new List<SiteLimit>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = userSid == null
                ? "SELECT Host, DailyMaxMinutes, Enabled, UserSid FROM UserSiteLimits ORDER BY UserSid, Host"
                : "SELECT Host, DailyMaxMinutes, Enabled, UserSid FROM UserSiteLimits WHERE UserSid = @sid ORDER BY Host";
            if (userSid != null) cmd.Parameters.AddWithValue("@sid", userSid);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new SiteLimit { Host = r.GetString(0), DailyMaxMinutes = r.GetInt32(1), Enabled = r.GetBoolean(2), UserSid = r.GetString(3) });
            return list;
        }
        finally { _lock.Release(); }
    }

    public async Task SaveUserSiteLimitAsync(SiteLimit limit)
    {
        await _lock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO UserSiteLimits (UserSid, Host, DailyMaxMinutes, Enabled) VALUES (@sid, @host, @max, @en)
                ON CONFLICT(UserSid, Host) DO UPDATE SET DailyMaxMinutes = @max, Enabled = @en;
                """;
            cmd.Parameters.AddWithValue("@sid", limit.UserSid);
            cmd.Parameters.AddWithValue("@host", limit.Host);
            cmd.Parameters.AddWithValue("@max", limit.DailyMaxMinutes);
            cmd.Parameters.AddWithValue("@en", limit.Enabled ? 1 : 0);
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteUserSiteLimitAsync(string host, string userSid)
    {
        await _lock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM UserSiteLimits WHERE Host = @host AND UserSid = @sid";
            cmd.Parameters.AddWithValue("@host", host);
            cmd.Parameters.AddWithValue("@sid", userSid);
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _lock.Release(); }
    }

    public async Task ReplaceUserSiteLimitsAsync(IEnumerable<SiteLimit> limits)
    {
        await _lock.WaitAsync();
        try
        {
            using var tx = _connection.BeginTransaction();
            using (var clear = _connection.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = "DELETE FROM UserSiteLimits";
                await clear.ExecuteNonQueryAsync();
            }
            foreach (var limit in limits.Where(l => !string.IsNullOrEmpty(l.UserSid)))
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR REPLACE INTO UserSiteLimits (UserSid, Host, DailyMaxMinutes, Enabled) VALUES (@sid, @host, @max, @en)";
                cmd.Parameters.AddWithValue("@sid", limit.UserSid);
                cmd.Parameters.AddWithValue("@host", limit.Host);
                cmd.Parameters.AddWithValue("@max", limit.DailyMaxMinutes);
                cmd.Parameters.AddWithValue("@en", limit.Enabled ? 1 : 0);
                await cmd.ExecuteNonQueryAsync();
            }
            tx.Commit();
        }
        finally { _lock.Release(); }
    }

    /// <summary>Today's browsing time per site for one Windows account.</summary>
    public async Task<List<SiteUsageRecord>> GetUserSiteUsageTodayAsync(string userSid)
    {
        await _lock.WaitAsync();
        try
        {
            var list = new List<SiteUsageRecord>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT Host, Seconds FROM UserSiteUsage WHERE UserSid = @sid AND Date = @date";
            cmd.Parameters.AddWithValue("@sid", userSid);
            var today = MonitorAndControl.ScreenTime.ParentClock.Today;
            cmd.Parameters.AddWithValue("@date", today.ToString("yyyy-MM-dd"));
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new SiteUsageRecord { Host = r.GetString(0), Date = today, Seconds = r.GetInt64(1) });
            return list;
        }
        finally { _lock.Release(); }
    }

    public async Task<List<SiteLimit>> GetSiteLimitsAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var list = new List<SiteLimit>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT Host, DailyMaxMinutes, Enabled FROM SiteLimits ORDER BY Host";
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new SiteLimit { Host = r.GetString(0), DailyMaxMinutes = r.GetInt32(1), Enabled = r.GetBoolean(2) });
            return list;
        }
        finally { _lock.Release(); }
    }

    public async Task SaveSiteLimitAsync(SiteLimit limit)
    {
        await _lock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO SiteLimits (Host, DailyMaxMinutes, Enabled) VALUES (@host, @max, @en)
                ON CONFLICT(Host) DO UPDATE SET DailyMaxMinutes = @max, Enabled = @en;
                """;
            cmd.Parameters.AddWithValue("@host", limit.Host);
            cmd.Parameters.AddWithValue("@max", limit.DailyMaxMinutes);
            cmd.Parameters.AddWithValue("@en", limit.Enabled ? 1 : 0);
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteSiteLimitAsync(string host)
    {
        await _lock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM SiteLimits WHERE Host = @host";
            cmd.Parameters.AddWithValue("@host", host);
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _lock.Release(); }
    }

    public async Task ReplaceSiteLimitsAsync(IEnumerable<SiteLimit> limits)
    {
        await _lock.WaitAsync();
        try
        {
            using var tx = _connection.BeginTransaction();
            using (var clear = _connection.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = "DELETE FROM SiteLimits";
                await clear.ExecuteNonQueryAsync();
            }
            foreach (var limit in limits)
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR REPLACE INTO SiteLimits (Host, DailyMaxMinutes, Enabled) VALUES (@host, @max, @en)";
                cmd.Parameters.AddWithValue("@host", limit.Host);
                cmd.Parameters.AddWithValue("@max", limit.DailyMaxMinutes);
                cmd.Parameters.AddWithValue("@en", limit.Enabled ? 1 : 0);
                await cmd.ExecuteNonQueryAsync();
            }
            tx.Commit();
        }
        finally { _lock.Release(); }
    }

    public async Task AddSiteUsageAsync(DateTime date, IReadOnlyDictionary<string, long> secondsByHost, string userSid = "")
    {
        await _lock.WaitAsync();
        try
        {
            using var tx = _connection.BeginTransaction();
            foreach (var (host, seconds) in secondsByHost)
            {
                if (seconds <= 0 || string.IsNullOrWhiteSpace(host)) continue;
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO SiteUsage (Host, Date, Seconds) VALUES (@host, @date, @seconds)
                    ON CONFLICT(Host, Date) DO UPDATE SET Seconds = Seconds + @seconds;
                    """;
                cmd.Parameters.AddWithValue("@host", host);
                cmd.Parameters.AddWithValue("@date", date.ToString("yyyy-MM-dd"));
                cmd.Parameters.AddWithValue("@seconds", seconds);
                await cmd.ExecuteNonQueryAsync();
                if (userSid.Length > 0)
                {
                    using var own = _connection.CreateCommand();
                    own.Transaction = tx;
                    own.CommandText = """
                        INSERT INTO UserSiteUsage (UserSid, Host, Date, Seconds) VALUES (@sid, @host, @date, @seconds)
                        ON CONFLICT(UserSid, Host, Date) DO UPDATE SET Seconds = Seconds + @seconds;
                        """;
                    own.Parameters.AddWithValue("@sid", userSid);
                    own.Parameters.AddWithValue("@host", host);
                    own.Parameters.AddWithValue("@date", date.ToString("yyyy-MM-dd"));
                    own.Parameters.AddWithValue("@seconds", seconds);
                    await own.ExecuteNonQueryAsync();
                }
                await AddActivityAsync(tx, "site", host, seconds);
            }
            tx.Commit();
        }
        finally { _lock.Release(); }
    }

    // Callers hold the database lock. The hour is the local clock hour the time was recorded in.
    private async Task AddActivityAsync(Microsoft.Data.Sqlite.SqliteTransaction tx, string kind, string name, long seconds)
    {
        if (seconds <= 0 || string.IsNullOrWhiteSpace(name)) return;
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO ActivityHours (Hour, Kind, Name, Seconds) VALUES (@hour, @kind, @name, @seconds)
            ON CONFLICT(Hour, Kind, Name) DO UPDATE SET Seconds = Seconds + @seconds;
            """;
        cmd.Parameters.AddWithValue("@hour", MonitorAndControl.ScreenTime.ParentClock.Now.ToString("yyyy-MM-dd HH"));
        cmd.Parameters.AddWithValue("@kind", kind);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@seconds", seconds);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<ActivityHourRecord>> GetActivityDayAsync(DateTime day)
    {
        await _lock.WaitAsync();
        try
        {
            var list = new List<ActivityHourRecord>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT Hour, Kind, Name, Seconds FROM ActivityHours WHERE Hour >= @from AND Hour < @to ORDER BY Hour, Seconds DESC";
            cmd.Parameters.AddWithValue("@from", day.ToString("yyyy-MM-dd") + " 00");
            cmd.Parameters.AddWithValue("@to", day.AddDays(1).ToString("yyyy-MM-dd") + " 00");
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new ActivityHourRecord { Hour = int.Parse(r.GetString(0)[^2..]), Kind = r.GetString(1), Name = r.GetString(2), Seconds = r.GetInt64(3) });
            return list;
        }
        finally { _lock.Release(); }
    }

    public async Task<List<SiteUsageRecord>> GetSiteUsageRangeAsync(DateTime from, DateTime to)
    {
        await _lock.WaitAsync();
        try
        {
            var list = new List<SiteUsageRecord>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT Host, Date, Seconds FROM SiteUsage WHERE Date >= @from AND Date <= @to ORDER BY Seconds DESC";
            cmd.Parameters.AddWithValue("@from", from.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("@to", to.ToString("yyyy-MM-dd"));
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new SiteUsageRecord { Host = r.GetString(0), Date = DateTime.Parse(r.GetString(1)), Seconds = r.GetInt64(2) });
            return list;
        }
        finally { _lock.Release(); }
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
            cmd.CommandText = "DELETE FROM UsageRecords; DELETE FROM LimitGroupUsage; DELETE FROM LimitGroupUsageArchive; DELETE FROM SiteUsage; DELETE FROM ActivityHours; DELETE FROM UserAppUsage";
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
            var date = MonitorAndControl.ScreenTime.ParentClock.Today.ToString("yyyy-MM-dd");

            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM UsageRecords WHERE Date = @date; DELETE FROM UserAppUsage WHERE Date = @date";
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
            cmd.Parameters.AddWithValue("@date", MonitorAndControl.ScreenTime.ParentClock.Today.ToString("yyyy-MM-dd"));
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
            cmd.Parameters.AddWithValue("@date", MonitorAndControl.ScreenTime.ParentClock.Today.ToString("yyyy-MM-dd"));
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
            cmd.Parameters.AddWithValue("@date", MonitorAndControl.ScreenTime.ParentClock.Today.ToString("yyyy-MM-dd"));
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
            cmd.Parameters.AddWithValue("@date", MonitorAndControl.ScreenTime.ParentClock.Today.ToString("yyyy-MM-dd"));
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
            cmd.CommandText = "SELECT Id, AppName, DayOfWeek, StartTime, EndTime, Enabled, UserSid FROM ScheduleRules";
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new ScheduleRule
                {
                    Id = r.GetInt32(0),
                    AppName = r.GetString(1),
                    DayOfWeek = r.GetString(2),
                    StartTime = r.GetString(3),
                    EndTime = r.GetString(4),
                    Enabled = r.GetBoolean(5),
                    UserSid = r.GetString(6)
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
                INSERT INTO ScheduleRules (AppName, DayOfWeek, StartTime, EndTime, Enabled, UserSid)
                VALUES (@app, @day, @start, @end, @en, @sid);
                """;
            cmd.Parameters.AddWithValue("@app", rule.AppName ?? "");
            cmd.Parameters.AddWithValue("@day", rule.DayOfWeek);
            cmd.Parameters.AddWithValue("@start", rule.StartTime);
            cmd.Parameters.AddWithValue("@end", rule.EndTime);
            cmd.Parameters.AddWithValue("@en", rule.Enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("@sid", rule.UserSid ?? "");
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
                UPDATE ScheduleRules SET AppName=@app, DayOfWeek=@day, StartTime=@start, EndTime=@end, Enabled=@en, UserSid=@sid
                WHERE Id=@id;
                """;
            cmd.Parameters.AddWithValue("@sid", rule.UserSid ?? "");
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

    // ---- Limits tamper guard -------------------------------------------------------------
    // This process is the only one that should ever write monitor.db. SQLite's data_version
    // changes only when a *different* connection modifies the file, so a child editing the limits
    // with another tool shows up as "version changed AND limits differ from what we last saw".
    // Our own legitimate edits never change data_version, so each quiet check just refreshes the
    // baseline. Limitation: this guard lives in the child's own process; the service-side copy
    // would be needed to survive DeviceMon being killed first.
    private (List<AppLimitRule> Limits, List<ScheduleRule> Schedule, string Fingerprint)? _limitsBaseline;
    private long _limitsDataVersion = -1;

    private async Task<long> ReadDataVersionAsync()
    {
        await _lock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "PRAGMA data_version";
            return Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }
        finally { _lock.Release(); }
    }

    public static string LimitsFingerprint(IEnumerable<AppLimitRule> limits, IEnumerable<ScheduleRule> schedule)
    {
        var lines = limits.Select(l => $"L|{l.AppName}|{l.DailyMaxMinutes}|{(l.Enabled ? 1 : 0)}")
            .Concat(schedule.Select(r => $"S|{r.AppName}|{r.DayOfWeek}|{r.StartTime}|{r.EndTime}|{(r.Enabled ? 1 : 0)}|{r.UserSid}"))
            .OrderBy(x => x, StringComparer.Ordinal);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join(';', lines))));
    }

    /// <summary>
    /// Returns a description when the limits or schedule were changed behind this process's back
    /// (and puts the previous values back), otherwise null.
    /// </summary>
    public async Task<string?> CheckLimitsIntegrityAsync()
    {
        var version = await ReadDataVersionAsync();
        var limits = await GetLimitRulesAsync();
        var schedule = await GetScheduleRulesAsync();
        var fingerprint = LimitsFingerprint(limits, schedule);

        if (_limitsBaseline is not { } baseline || version == _limitsDataVersion)
        {
            _limitsBaseline = (limits, schedule, fingerprint);
            _limitsDataVersion = version;
            return null;
        }

        _limitsDataVersion = version;
        if (fingerprint == baseline.Fingerprint) return null;

        await ReplaceLimitRulesAsync(baseline.Limits);
        await ReplaceScheduleRulesAsync(baseline.Schedule);
        _limitsDataVersion = await ReadDataVersionAsync();
        return $"The app limits or schedule in the database were changed outside DeviceMon " +
               $"(was {baseline.Limits.Count} limits and {baseline.Schedule.Count} schedule rules, " +
               $"found {limits.Count} and {schedule.Count}). The previous values were restored.";
    }

    // ---- Sealed limits ---------------------------------------------------------------------
    /// <summary>
    /// Takes a time-limited claim on a named job, so only one signed-in account's DeviceMon runs it at a time.
    /// The check and the claim are a single statement, so two processes cannot both win.
    /// </summary>
    public async Task<bool> TryClaimLeaseAsync(string key, TimeSpan duration)
    {
        await _lock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "INSERT INTO Settings (Key, Value) VALUES (@k, @until) ON CONFLICT(Key) DO UPDATE SET Value = @until WHERE Value < @now";
            cmd.Parameters.AddWithValue("@k", "Lease:" + key);
            cmd.Parameters.AddWithValue("@until", DateTime.UtcNow.Add(duration).ToString("O"));
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
            return await cmd.ExecuteNonQueryAsync() > 0;
        }
        finally { _lock.Release(); }
    }

    public async Task<List<string>> GetHomeworkAppsAsync()
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<List<string>>(await GetSettingAsync("HomeworkBlockedApps", "[]")) ?? new(); }
        catch { return new(); }
    }

    public Task SetHomeworkAppsAsync(IEnumerable<string> apps) =>
        SetSettingAsync("HomeworkBlockedApps", System.Text.Json.JsonSerializer.Serialize(apps.Distinct(StringComparer.OrdinalIgnoreCase).ToList()));

    public async Task<List<string>> GetAlwaysAllowedAppsAsync()
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<List<string>>(await GetSettingAsync("AlwaysAllowedApps", "[]")) ?? new(); }
        catch { return new(); }
    }

    public Task SetAlwaysAllowedAppsAsync(IEnumerable<string> apps) =>
        SetSettingAsync("AlwaysAllowedApps", System.Text.Json.JsonSerializer.Serialize(apps.Distinct(StringComparer.OrdinalIgnoreCase).ToList()));

    public void ResetLimitsBaseline() { _limitsBaseline = null; _limitsDataVersion = -1; }

    public async Task<MonitorAndControl.ScreenTime.SealedLimits> ReadLimitsSnapshotAsync()
    {
        var snapshot = new MonitorAndControl.ScreenTime.SealedLimits();
        foreach (var l in await GetLimitRulesAsync())
            snapshot.Limits.Add(new() { App = l.AppName, Minutes = l.DailyMaxMinutes, Enabled = l.Enabled });
        foreach (var r in await GetScheduleRulesAsync())
            snapshot.Schedule.Add(new() { App = r.AppName, Day = r.DayOfWeek, Start = r.StartTime, End = r.EndTime, Enabled = r.Enabled, User = r.UserSid });
        snapshot.HomeworkApps = await GetHomeworkAppsAsync();
        snapshot.AlwaysAllowed = await GetAlwaysAllowedAppsAsync();
        snapshot.AllowOnly = await GetSettingAsync("SiteAllowOnly", "false") == "true";
        snapshot.SafeBrowsing = await GetSettingAsync("SafeBrowsing", "false") == "true";
        foreach (var u in await GetUserLimitRulesAsync())
            snapshot.UserLimits.Add(new() { User = u.UserSid, App = u.AppName, Minutes = u.DailyMaxMinutes, Enabled = u.Enabled });
        foreach (var w in await GetSiteLimitsAsync())
            snapshot.Sites.Add(new() { Host = w.Host, Minutes = w.DailyMaxMinutes, Enabled = w.Enabled });
        foreach (var w in await GetUserSiteLimitsAsync())
            snapshot.Sites.Add(new() { Host = w.Host, Minutes = w.DailyMaxMinutes, Enabled = w.Enabled, User = w.UserSid });
        foreach (var g in await GetLimitGroupsAsync())
            snapshot.Groups.Add(new() { Name = g.Name, Minutes = g.DailyMaxMinutes, Enabled = g.Enabled, Members = g.AppNames.ToList(), User = g.UserSid });
        return snapshot;
    }

    /// <summary>Puts limits, schedule and groups back exactly as sealed. Groups are matched by name so their usage history survives.</summary>
    public async Task ApplyLimitsSnapshotAsync(MonitorAndControl.ScreenTime.SealedLimits snapshot)
    {
        await ReplaceLimitRulesAsync(snapshot.Limits.Select(l => new AppLimitRule { AppName = l.App, DailyMaxMinutes = l.Minutes, Enabled = l.Enabled }));
        await ReplaceScheduleRulesAsync(snapshot.Schedule.Select(s => new ScheduleRule
            { AppName = s.App, DayOfWeek = s.Day, StartTime = s.Start, EndTime = s.End, Enabled = s.Enabled, UserSid = s.User }));
        await ReplaceUserLimitRulesAsync(snapshot.UserLimits.Select(u => new AppLimitRule { UserSid = u.User, AppName = u.App, DailyMaxMinutes = u.Minutes, Enabled = u.Enabled }));
        await SetHomeworkAppsAsync(snapshot.HomeworkApps);
        await SetAlwaysAllowedAppsAsync(snapshot.AlwaysAllowed);
        await SetSettingAsync("SiteAllowOnly", snapshot.AllowOnly ? "true" : "false");
        await SetSettingAsync("SafeBrowsing", snapshot.SafeBrowsing ? "true" : "false");
        await ReplaceSiteLimitsAsync(snapshot.Sites.Where(w => string.IsNullOrEmpty(w.User)).Select(w => new SiteLimit { Host = w.Host, DailyMaxMinutes = w.Minutes, Enabled = w.Enabled }));
        await ReplaceUserSiteLimitsAsync(snapshot.Sites.Where(w => !string.IsNullOrEmpty(w.User)).Select(w => new SiteLimit { Host = w.Host, DailyMaxMinutes = w.Minutes, Enabled = w.Enabled, UserSid = w.User }));
        var keep = new HashSet<string>(snapshot.Groups.Select(g => g.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var existing in await GetLimitGroupsAsync())
            if (!keep.Contains(existing.Name)) await DeleteLimitGroupAsync(existing.Id);
        foreach (var g in snapshot.Groups)
            await SaveLimitGroupAsync(new AppLimitGroup { Name = g.Name, DailyMaxMinutes = g.Minutes, Enabled = g.Enabled, AppNames = g.Members.ToList(), UserSid = g.User });
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
                    INSERT INTO ScheduleRules (AppName, DayOfWeek, StartTime, EndTime, Enabled, UserSid)
                    VALUES (@app, @day, @start, @end, @en, @sid);
                    """;
                cmd.Parameters.AddWithValue("@sid", rule.UserSid ?? "");
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
            cmd.CommandText = "DELETE FROM UsageRecords WHERE AppName = @app; DELETE FROM UserAppUsage WHERE AppName = @app; DELETE FROM AppUserLimits WHERE AppName = @app";
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
        IEnumerable<(int GroupId, int Seconds)> groupRecords,
        string userSid = "")
    {
        await _lock.WaitAsync();
        try
        {
            using var tx = _connection.BeginTransaction();
            var date = MonitorAndControl.ScreenTime.ParentClock.Today.ToString("yyyy-MM-dd");
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
            foreach (var record in appRecords)
                if (record.ForegroundSeconds > 0) await AddActivityAsync(tx, "app", record.AppName, record.ForegroundSeconds);
            // Each Windows account's own time, so a limit can apply to one account only.
            if (!string.IsNullOrEmpty(userSid))
                foreach (var record in appRecords)
                {
                    var total = record.ForegroundSeconds + record.BackgroundSeconds;
                    if (total <= 0) continue;
                    using var cmd = _connection.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        INSERT INTO UserAppUsage (UserSid, AppName, Date, Seconds) VALUES (@sid, @app, @date, @seconds)
                        ON CONFLICT(UserSid, AppName, Date) DO UPDATE SET Seconds = Seconds + @seconds;
                        """;
                    cmd.Parameters.AddWithValue("@sid", userSid);
                    cmd.Parameters.AddWithValue("@app", record.AppName);
                    cmd.Parameters.AddWithValue("@date", date);
                    cmd.Parameters.AddWithValue("@seconds", total);
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
                       COALESCE(u.TotalSeconds, 0), g.UserSid
                FROM LimitGroups g
                LEFT JOIN LimitGroupUsage u ON u.GroupId = g.Id AND u.Date = @date
                ORDER BY g.Name;
                """;
            cmd.Parameters.AddWithValue("@date", MonitorAndControl.ScreenTime.ParentClock.Today.ToString("yyyy-MM-dd"));
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                groups.Add(new AppLimitGroup
                {
                    Id = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    DailyMaxMinutes = reader.GetInt32(2),
                    Enabled = reader.GetBoolean(3),
                    TodaySeconds = reader.GetInt64(4),
                    UserSid = reader.GetString(5)
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
                    ? "UPDATE LimitGroups SET Name=@name, DailyMaxMinutes=@max, Enabled=@enabled, UserSid=@sid WHERE Id=@id"
                    : "INSERT INTO LimitGroups (Name, DailyMaxMinutes, Enabled, UserSid) VALUES (@name, @max, @enabled, @sid) ON CONFLICT(Name) DO UPDATE SET DailyMaxMinutes=@max, Enabled=@enabled, UserSid=@sid";
                cmd.Parameters.AddWithValue("@name", group.Name);
                cmd.Parameters.AddWithValue("@sid", group.UserSid ?? "");
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
            cmd.Parameters.AddWithValue("@date", MonitorAndControl.ScreenTime.ParentClock.Today.ToString("yyyy-MM-dd"));
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
