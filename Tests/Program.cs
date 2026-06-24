using Microsoft.Data.Sqlite;
using MonitorAndControl.Data;
using MonitorAndControl.Models;
using MonitorAndControl.Services;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Scheduler allows time inside same-day window", TestSchedulerInsideSameDayWindow),
    ("Scheduler blocks time outside same-day window", TestSchedulerOutsideSameDayWindow),
    ("Scheduler handles overnight windows", TestSchedulerOvernightWindow),
    ("Scheduler finds next allowed time", TestSchedulerNextAllowedTime),
    ("Scheduler finds current allowed window end", TestSchedulerCurrentAllowedWindowEnd),
    ("Usage database accumulates daily app usage", TestUsageDatabaseAccumulatesUsage),
    ("Usage database separates foreground and background time", TestUsageDatabaseTracksUsageSources),
    ("Usage database preserves legacy unclassified totals", TestUsageDatabasePreservesLegacyTotals),
    ("Usage database clears today's usage", TestUsageDatabaseClearsToday),
    ("Usage database tracks and clears daily bonus time", TestUsageDatabaseTracksBonusTime),
    ("Usage database stores per-app schedule targets", TestUsageDatabaseStoresScheduleTarget),
    ("Usage database stores app tracking policies", TestUsageDatabaseStoresTrackingPolicy),
    ("Usage database stores shared limit groups and usage", TestUsageDatabaseStoresLimitGroups),
    ("Defaults are imported only on first database creation", TestDefaultsImportedOnlyOnce),
    ("Existing empty databases do not reimport defaults", TestExistingDatabaseDoesNotReimportDefaults),
    ("Usage database replaces backup-managed config tables", TestUsageDatabaseReplacesConfigTables),
    ("Window tracker returns every process mapped to an app", TestWindowTrackerReturnsAllAppProcesses),
    ("Window tracker validates idle tracking settings", TestWindowTrackerIdleSettings),
    ("Daily summaries calculate latest missed delivery", TestDailySummaryDueTime),
    ("Weekly summaries calculate latest missed delivery", TestWeeklySummaryDueTime),
    ("Monthly summaries clamp delivery days", TestMonthlySummaryDueTime),
    ("Limit enforcer rehydrates exceeded apps", TestLimitEnforcerRehydratesExceededApps),
    ("Limit enforcer rehydrates exceeded groups", TestLimitEnforcerRehydratesExceededGroups),
    ("Limit enforcer blocks inactive group members without warnings", TestLimitEnforcerBlocksInactiveGroup),
    ("Limit enforcer starts one countdown per breached group", TestLimitEnforcerStartsSingleGroupCountdown),
    ("Limit enforcer can pause and resume enforcement", TestLimitEnforcerPauseResume)
};

var failures = new List<string>();

foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{test.Name}: {ex.Message}");
        Console.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}

if (failures.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("Failures:");
    foreach (var failure in failures)
        Console.WriteLine($"- {failure}");
    Environment.Exit(1);
}

static Task TestSchedulerInsideSameDayWindow()
{
    var rule = Rule("Everyday", "15:00", "21:00");
    var now = new DateTime(2026, 6, 16, 16, 30, 0);

    AssertFalse(SchedulerService.IsOutsideAllowedHours(rule, now), "16:30 should be allowed.");
    return Task.CompletedTask;
}

static Task TestSchedulerOutsideSameDayWindow()
{
    var rule = Rule("Everyday", "15:00", "21:00");
    var now = new DateTime(2026, 6, 16, 22, 0, 0);

    AssertTrue(SchedulerService.IsOutsideAllowedHours(rule, now), "22:00 should be blocked.");
    return Task.CompletedTask;
}

static Task TestSchedulerOvernightWindow()
{
    var rule = Rule("Everyday", "22:00", "06:00");

    AssertFalse(
        SchedulerService.IsOutsideAllowedHours(rule, new DateTime(2026, 6, 16, 23, 0, 0)),
        "23:00 should be allowed for an overnight window.");
    AssertFalse(
        SchedulerService.IsOutsideAllowedHours(rule, new DateTime(2026, 6, 16, 5, 30, 0)),
        "05:30 should be allowed for an overnight window.");
    AssertTrue(
        SchedulerService.IsOutsideAllowedHours(rule, new DateTime(2026, 6, 16, 12, 0, 0)),
        "12:00 should be blocked for an overnight window.");

    return Task.CompletedTask;
}

static Task TestSchedulerNextAllowedTime()
{
    var rules = new[]
    {
        Rule("Everyday", "15:00", "21:00")
    };
    var now = new DateTime(2026, 6, 16, 12, 0, 0);
    var next = SchedulerService.GetNextAllowedTime(rules, now);

    AssertEqual(new DateTime(2026, 6, 16, 15, 0, 0), next, "Expected next allowed time today.");

    now = new DateTime(2026, 6, 16, 22, 0, 0);
    next = SchedulerService.GetNextAllowedTime(rules, now);
    AssertEqual(new DateTime(2026, 6, 17, 15, 0, 0), next, "Expected next allowed time tomorrow.");

    return Task.CompletedTask;
}

static Task TestSchedulerCurrentAllowedWindowEnd()
{
    var rules = new[]
    {
        Rule("Everyday", "15:00", "21:00"),
        new ScheduleRule
        {
            AppName = "Chess",
            DayOfWeek = "Everyday",
            StartTime = "18:00",
            EndTime = "22:00",
            Enabled = true
        }
    };
    var now = new DateTime(2026, 6, 16, 19, 0, 0);
    var end = SchedulerService.GetCurrentAllowedWindowEnd(rules, "Chess", now);

    AssertEqual(new DateTime(2026, 6, 16, 22, 0, 0), end, "Expected app-specific later window end.");
    AssertEqual(null, SchedulerService.GetCurrentAllowedWindowEnd(rules, "Chess", new DateTime(2026, 6, 16, 23, 0, 0)), "Expected no active allowed window.");

    return Task.CompletedTask;
}

static async Task TestUsageDatabaseAccumulatesUsage()
{
    using var db = CreateTempDatabase();

    await db.RecordUsageAsync("Chess", "chess.exe", 30);
    await db.RecordUsageAsync("Chess", "chess.exe", 45);

    var today = await db.GetTodayUsageAsync();
    var record = today.SingleOrDefault(x => x.AppName == "Chess");

    AssertTrue(record != null, "Expected a usage record for Chess.");
    AssertEqual(75L, record!.TotalSeconds, "Usage seconds should accumulate.");
    AssertEqual(75L, record.ForegroundSeconds, "Existing recording API should classify usage as foreground.");
    AssertEqual(0L, record.BackgroundSeconds, "Existing recording API should not add background time.");
    AssertEqual(0L, record.UnclassifiedSeconds, "New usage should be fully classified.");
}

static async Task TestUsageDatabaseTracksUsageSources()
{
    using var db = CreateTempDatabase();

    await db.RecordUsageAsync("Discord", "discord.exe", 15, 45);
    await db.RecordUsageAsync("Discord", "discord.exe", 5, 10);

    var record = (await db.GetTodayUsageAsync()).Single();
    AssertEqual(75L, record.TotalSeconds, "Total should include foreground and background time.");
    AssertEqual(20L, record.ForegroundSeconds, "Foreground time should accumulate separately.");
    AssertEqual(55L, record.BackgroundSeconds, "Background time should accumulate separately.");
    AssertEqual(0L, record.UnclassifiedSeconds, "Source-aware usage should be fully classified.");
}

static async Task TestUsageDatabasePreservesLegacyTotals()
{
    var path = Path.Combine(Path.GetTempPath(), "MonitorAndControlTests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    await using (var connection = new SqliteConnection($"Data Source={path}"))
    {
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE UsageRecords (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                AppName TEXT NOT NULL,
                ProcessName TEXT NOT NULL,
                Date TEXT NOT NULL,
                TotalSeconds INTEGER NOT NULL DEFAULT 0,
                UNIQUE(AppName, Date)
            );
            INSERT INTO UsageRecords (AppName, ProcessName, Date, TotalSeconds)
            VALUES ('Legacy App', 'legacy.exe', @date, 90);
            """;
        command.Parameters.AddWithValue("@date", DateTime.Today.ToString("yyyy-MM-dd"));
        await command.ExecuteNonQueryAsync();
    }

    using var db = new UsageDatabase(path);
    var record = (await db.GetTodayUsageAsync()).Single();
    AssertEqual(90L, record.TotalSeconds, "Migration must preserve the legacy total.");
    AssertEqual(0L, record.ForegroundSeconds, "Legacy time must not be mislabeled as foreground.");
    AssertEqual(0L, record.BackgroundSeconds, "Legacy time must not be mislabeled as background.");
    AssertEqual(90L, record.UnclassifiedSeconds, "Legacy time should be exposed as unclassified.");
}

static async Task TestLimitEnforcerRehydratesExceededApps()
{
    using var db = CreateTempDatabase();
    var tracker = new WindowTracker();
    tracker.AddKnownApp("chess.exe", "Chess");
    var enforcer = new LimitEnforcer(db, tracker);

    await db.SaveLimitRuleAsync(new AppLimitRule
    {
        AppName = "Chess",
        DailyMaxMinutes = 1,
        Enabled = true
    });
    await db.RecordUsageAsync("Chess", "chess.exe", 60);

    await enforcer.RehydrateExceededTodayAsync();

    AssertTrue(enforcer.IsExceededToday("Chess"), "Chess should be marked exceeded after rehydrate.");
    tracker.Dispose();
}

static async Task TestUsageDatabaseStoresScheduleTarget()
{
    using var db = CreateTempDatabase();

    await db.SaveScheduleRuleAsync(new ScheduleRule
    {
        AppName = "Chess",
        DayOfWeek = "Everyday",
        StartTime = "15:00",
        EndTime = "21:00",
        Enabled = true
    });

    var rules = await db.GetScheduleRulesAsync();
    var rule = rules.SingleOrDefault();

    AssertTrue(rule != null, "Expected one schedule rule.");
    AssertEqual("Chess", rule!.AppName, "Schedule target should round-trip.");
}

static async Task TestLimitEnforcerRehydratesExceededGroups()
{
    using var db = CreateTempDatabase();
    var tracker = new WindowTracker();
    tracker.AddKnownApp("chess.exe", "Chess");
    tracker.AddKnownApp("cards.exe", "Cards");
    var enforcer = new LimitEnforcer(db, tracker);

    await db.SaveLimitGroupAsync(new AppLimitGroup
    {
        Name = "Games",
        DailyMaxMinutes = 1,
        Enabled = true,
        AppNames = new List<string> { "Chess", "Cards" }
    });
    var group = (await db.GetLimitGroupsAsync()).Single();
    await db.RecordLimitGroupUsageAsync(group.Id, 60);

    await enforcer.RehydrateExceededTodayAsync();

    AssertTrue(enforcer.IsExceededToday("Chess"), "Every member should be blocked after a group reaches its limit.");
    AssertTrue(enforcer.IsExceededToday("Cards"), "Every member should be blocked after a group reaches its limit.");
    tracker.Dispose();
}

static async Task TestUsageDatabaseStoresLimitGroups()
{
    using var db = CreateTempDatabase();
    await db.SaveLimitGroupAsync(new AppLimitGroup
    {
        Name = "Games",
        DailyMaxMinutes = 180,
        Enabled = true,
        AppNames = new List<string> { "Chess", "Cards" }
    });

    var group = (await db.GetLimitGroupsAsync()).Single();
    AssertEqual("Games", group.Name, "Group name should round-trip.");
    AssertEqual(2, group.AppNames.Count, "Group members should round-trip.");
    await db.RecordLimitGroupUsageAsync(group.Id, 45);
    group = (await db.GetLimitGroupsAsync()).Single();
    AssertEqual(45L, group.TodaySeconds, "Group usage should accumulate independently.");
    var history = await db.GetLimitGroupUsageRangeAsync(DateTime.Today, DateTime.Today);
    AssertEqual(1, history.Count, "Group usage should be available in history.");
    AssertEqual("Games", history[0].GroupName, "Group history should include its name.");
    AssertEqual(45L, history[0].TotalSeconds, "Group history should preserve shared elapsed time.");

    await db.ReplaceLimitGroupsAsync(new[]
    {
        new AppLimitGroup
        {
            Name = "Games",
            DailyMaxMinutes = 120,
            Enabled = true,
            AppNames = new List<string> { "Chess" }
        }
    });
    group = (await db.GetLimitGroupsAsync()).Single();
    AssertEqual(45L, group.TodaySeconds, "Replacing group definitions must preserve today's shared usage.");
    await db.RemoveAppFromLimitGroupsAsync("Chess");
    group = (await db.GetLimitGroupsAsync()).Single();
    AssertEqual(0, group.AppNames.Count, "Forgetting the last app mapping should remove stale group membership.");

    await db.ClearTodayUsageAsync();
    group = (await db.GetLimitGroupsAsync()).Single();
    AssertEqual(0L, group.TodaySeconds, "Clearing today should clear group usage.");
}

static async Task TestLimitEnforcerBlocksInactiveGroup()
{
    using var db = CreateTempDatabase();
    using var tracker = new WindowTracker();
    tracker.AddKnownApp("missing-chess.exe", "Chess");
    tracker.AddKnownApp("missing-cards.exe", "Cards");
    var enforcer = new LimitEnforcer(db, tracker);
    var alerts = 0;
    enforcer.OnBreachAlert += (_, _, _) => alerts++;

    await enforcer.EnforceAsync(
        new List<(string AppName, long UsedSecs, long MaxSecs)>(),
        new List<GroupLimitBreach>
        {
            new(1, "Games", new[] { "Chess", "Cards" }, 60, 60)
        },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    AssertTrue(enforcer.IsExceededToday("Chess"), "Inactive group members should be blocked immediately.");
    AssertTrue(enforcer.IsExceededToday("Cards"), "Every inactive group member should be blocked.");
    AssertEqual(0, alerts, "No warning should be shown when no group member is running.");
}

static async Task TestLimitEnforcerStartsSingleGroupCountdown()
{
    using var db = CreateTempDatabase();
    using var tracker = new WindowTracker();
    var currentProcess = Path.GetFileName(Environment.ProcessPath) ?? "MonitorAndControl.Tests.exe";
    tracker.AddKnownApp(currentProcess, "Chess");
    tracker.AddKnownApp("missing-cards.exe", "Cards");
    var enforcer = new LimitEnforcer(db, tracker);
    var alerts = new List<string>();
    enforcer.OnBreachAlert += (name, _, _) => alerts.Add(name);

    await enforcer.EnforceAsync(
        new List<(string AppName, long UsedSecs, long MaxSecs)>(),
        new List<GroupLimitBreach>
        {
            new(7, "Games", new[] { "Chess", "Cards" }, 60, 60)
        },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    await Task.Delay(200);
    enforcer.ClearGroup(7);

    AssertEqual(1, alerts.Count, "A breached group should create exactly one warning countdown.");
    AssertEqual("Games (group)", alerts[0], "The warning should identify the shared group.");
}

static async Task TestUsageDatabaseStoresTrackingPolicy()
{
    using var db = CreateTempDatabase();

    await db.SaveAppMappingAsync("discord.exe", "Discord", true, true);

    var mapping = (await db.GetAppMappingsAsync()).Single();
    AssertEqual("discord.exe", mapping.ProcessName, "Process name should round-trip.");
    AssertTrue(mapping.CountInBackground, "Background tracking should round-trip.");
    AssertTrue(mapping.IgnoreOverlayFocus, "Overlay focus filtering should round-trip.");

    await db.SaveAppMappingAsync("discord.exe", "Discord", false, false);
    mapping = (await db.GetAppMappingsAsync()).Single();
    AssertFalse(mapping.CountInBackground, "Background tracking should be updateable.");
    AssertFalse(mapping.IgnoreOverlayFocus, "Overlay focus filtering should be updateable.");
}

static async Task TestDefaultsImportedOnlyOnce()
{
    using var db = CreateTempDatabase();
    var config = ConfigWithDefaults();

    await db.InitializeDefaults(config);
    AssertEqual(1, (await db.GetLimitRulesAsync()).Count, "First start should import default limits.");
    var schedule = await db.GetScheduleRulesAsync();
    AssertEqual(1, schedule.Count, "First start should import default schedules.");

    await db.DeleteLimitRuleAsync("Chess");
    await db.DeleteScheduleRuleAsync(schedule[0].Id);
    await db.InitializeDefaults(config);

    AssertEqual(0, (await db.GetLimitRulesAsync()).Count, "Deleted limits must remain deleted after restart.");
    AssertEqual(0, (await db.GetScheduleRulesAsync()).Count, "Deleted schedules must remain deleted after restart.");
}

static async Task TestExistingDatabaseDoesNotReimportDefaults()
{
    var path = Path.Combine(Path.GetTempPath(), "MonitorAndControlTests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, Array.Empty<byte>());

    using var db = new UsageDatabase(path);
    await db.InitializeDefaults(ConfigWithDefaults());

    AssertEqual(0, (await db.GetLimitRulesAsync()).Count, "An existing empty database must stay empty during upgrade.");
    AssertEqual(0, (await db.GetScheduleRulesAsync()).Count, "Existing schedules must not be recreated during upgrade.");
}

static async Task TestUsageDatabaseReplacesConfigTables()
{
    using var db = CreateTempDatabase();

    await db.SaveAppMappingAsync("old.exe", "Old");
    await db.SaveLimitRuleAsync(new AppLimitRule { AppName = "Old", DailyMaxMinutes = 30, Enabled = true });
    await db.SaveScheduleRuleAsync(new ScheduleRule { AppName = "Old", DayOfWeek = "Everyday", StartTime = "10:00", EndTime = "11:00", Enabled = true });

    await db.ReplaceAppMappingsAsync(new[] { new AppMapping("new.exe", "New", true, true) });
    await db.ReplaceLimitRulesAsync(new[] { new AppLimitRule { AppName = "New", DailyMaxMinutes = 60, Enabled = false } });
    await db.ReplaceScheduleRulesAsync(new[] { new ScheduleRule { AppName = "New", DayOfWeek = "Weekend", StartTime = "12:00", EndTime = "13:00", Enabled = true } });

    var mappings = await db.GetAppMappingsAsync();
    var limits = await db.GetLimitRulesAsync();
    var schedules = await db.GetScheduleRulesAsync();

    AssertEqual(1, mappings.Count, "Expected one mapping after replace.");
    AssertEqual("New", mappings[0].AppName, "Expected replacement mapping.");
    AssertTrue(mappings[0].CountInBackground, "Expected replacement background policy.");
    AssertTrue(mappings[0].IgnoreOverlayFocus, "Expected replacement overlay policy.");
    AssertEqual(1, limits.Count, "Expected one limit after replace.");
    AssertEqual("New", limits[0].AppName, "Expected replacement limit.");
    AssertEqual(1, schedules.Count, "Expected one schedule after replace.");
    AssertEqual("New", schedules[0].AppName, "Expected replacement schedule.");
}

static async Task TestUsageDatabaseClearsToday()
{
    using var db = CreateTempDatabase();

    await db.RecordUsageAsync("Chess", "chess.exe", 30);
    await db.ClearTodayUsageAsync();

    var today = await db.GetTodayUsageAsync();
    AssertFalse(today.Any(x => x.AppName == "Chess"), "Expected today's Chess usage to be cleared.");
}

static async Task TestUsageDatabaseTracksBonusTime()
{
    using var db = CreateTempDatabase();

    var first = await db.AddTodayBonusMinutesAsync("Chess", 15);
    var second = await db.AddTodayBonusMinutesAsync("Chess", 30);
    var total = await db.GetTodayBonusMinutesAsync("Chess");
    var all = await db.GetTodayBonusTimeAsync();

    AssertEqual(15, first, "First bonus grant should return 15 minutes.");
    AssertEqual(45, second, "Second bonus grant should accumulate.");
    AssertEqual(45, total, "Bonus lookup should return accumulated minutes.");
    AssertEqual(1, all.Count, "Expected one bonus record.");

    await db.ClearTodayUsageAsync();
    AssertEqual(0, await db.GetTodayBonusMinutesAsync("Chess"), "Reset today should clear bonus time.");
}

static Task TestLimitEnforcerPauseResume()
{
    using var db = CreateTempDatabase();
    var tracker = new WindowTracker();
    var enforcer = new LimitEnforcer(db, tracker);

    var pausedUntil = enforcer.PauseFor(TimeSpan.FromMinutes(15));
    AssertTrue(enforcer.IsPaused, "Enforcement should be paused.");
    AssertTrue(pausedUntil > DateTimeOffset.Now, "Pause expiration should be in the future.");

    enforcer.Resume();
    AssertFalse(enforcer.IsPaused, "Enforcement should resume.");

    tracker.Dispose();
    return Task.CompletedTask;
}

static Task TestWindowTrackerReturnsAllAppProcesses()
{
    using var tracker = new WindowTracker();
    tracker.AddKnownApp("game-launcher.exe", "Game");
    tracker.AddKnownApp("game-client.exe", "Game");
    tracker.AddKnownApp("other.exe", "Other");

    var processes = tracker.GetProcessNamesForApp("game");
    AssertEqual(2, processes.Length, "Every process mapped to the same app should be returned.");
    AssertTrue(processes.Contains("game-launcher.exe"), "Expected launcher mapping.");
    AssertTrue(processes.Contains("game-client.exe"), "Expected client mapping.");
    return Task.CompletedTask;
}

static Task TestWindowTrackerIdleSettings()
{
    using var tracker = new WindowTracker();
    tracker.ConfigureIdleTracking(true, 0);
    AssertTrue(tracker.PauseWhenIdle, "Idle pausing should be enabled.");
    AssertEqual(1, tracker.IdleThresholdMinutes, "Idle threshold should clamp to one minute.");
    tracker.ConfigureIdleTracking(false, 500);
    AssertFalse(tracker.PauseWhenIdle, "Idle pausing should be disableable.");
    AssertEqual(240, tracker.IdleThresholdMinutes, "Idle threshold should clamp to 240 minutes.");
    return Task.CompletedTask;
}

static Task TestDailySummaryDueTime()
{
    var due = ParentReportService.GetLatestDue(
        new DateTime(2026, 6, 22, 17, 0, 0), "daily", TimeSpan.FromHours(18), 0, 1);
    AssertEqual(new DateTime(2026, 6, 21, 18, 0, 0), due, "Expected yesterday's missed daily delivery.");
    return Task.CompletedTask;
}

static Task TestWeeklySummaryDueTime()
{
    var due = ParentReportService.GetLatestDue(
        new DateTime(2026, 6, 22, 17, 0, 0), "weekly", TimeSpan.FromHours(18), 0, 1);
    AssertEqual(new DateTime(2026, 6, 21, 18, 0, 0), due, "Expected the latest Sunday delivery.");
    return Task.CompletedTask;
}

static Task TestMonthlySummaryDueTime()
{
    var due = ParentReportService.GetLatestDue(
        new DateTime(2026, 6, 22, 17, 0, 0), "monthly", TimeSpan.FromHours(18), 0, 31);
    AssertEqual(new DateTime(2026, 5, 31, 18, 0, 0), due, "Expected the previous valid month-end delivery.");
    return Task.CompletedTask;
}

static ScheduleRule Rule(string day, string start, string end) => new()
{
    DayOfWeek = day,
    StartTime = start,
    EndTime = end,
    Enabled = true
};

static AppConfig ConfigWithDefaults() => new()
{
    DefaultLimits = new List<AppLimitRule>
    {
        new() { AppName = "Chess", DailyMaxMinutes = 60, Enabled = true }
    },
    Schedule = new List<ScheduleRule>
    {
        new() { DayOfWeek = "Everyday", StartTime = "15:00", EndTime = "21:00", Enabled = true }
    }
};

static UsageDatabase CreateTempDatabase()
{
    var path = Path.Combine(Path.GetTempPath(), "MonitorAndControlTests", $"{Guid.NewGuid():N}.db");
    return new UsageDatabase(path);
}

static void AssertTrue(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void AssertFalse(bool condition, string message)
{
    if (condition)
        throw new InvalidOperationException(message);
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{message} Expected {expected}, got {actual}.");
}
