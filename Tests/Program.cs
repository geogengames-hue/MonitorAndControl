using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using MonitorAndControl.Data;
using MonitorAndControl.Models;
using MonitorAndControl.Services;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Screen time HTTP rejects foreign origin, host and invalid bodies", TestScreenTimeHttpBoundary),
    ("Hotkey failures are explained in plain language", TestHotKeyErrorDescriptions),
    ("Update status is read regardless of JSON key casing", TestUpdateStatusCasing),
    ("Remote PIN is stored hashed, verified, and survives restarts", TestRemotePinVerifyAndStorage),
    ("Remote PIN locks out guessing, with doubling lockouts", TestRemotePinLockout),
    ("Remote PIN rejects weak values", TestRemotePinRules),
    ("Weekend limit applies on Saturday and Sunday only", TestWeekendLimit),
    ("Account usage ignores capitals in the app name", TestAccountUsageCase),
    ("A claimed job runs in one account at a time", TestLeaseClaim),
    ("The time-left view reads clearly", TestTimeLeftView),
    ("The time zone is pinned until a parent accepts a change", TestPinnedTimeZone),
    ("Check-in addresses must be public https", TestHeartbeatUrls),
    ("Alert buttons: deep links, one-tap decline and Telegram keys", TestAlertButtons),
    ("Not now links work once and only before they expire", TestQuickActions),
    ("Groups, schedule and website rules can belong to one account", TestPerAccountRules),
    ("Older databases are upgraded for per-account groups", TestGroupMembersUpgrade),
    ("App limits can belong to one Windows account", TestPerAccountLimits),
    ("Homework mode times, plan and sealed apps", TestHomeworkMode),
    ("New programs are found once and described briefly", TestNewApps),
    ("Alerts link to the right dashboard page", TestPushLinks),
    ("Phone notification settings are validated", TestPushValidation),
    ("Activity timeline stores foreground time by hour", TestActivityTimeline),
    ("Browser policy plan combines reports with the sealed copy", TestPolicyPlan),
    ("Review fixes: bedtime kinds, restart dedupe, null-safe seal", TestReviewFixes),
    ("Website limits block outright or after the daily allowance", TestSiteLimitLogic),
    ("Browser block lists keep entries that are not ours", TestBlocklistMerge),
    ("Sealed limits include website rules", TestSealedSites),
    ("Site names keep only the host of a normal website", TestSiteNames),
    ("Website time is stored per host and day", TestSiteUsageStorage),
    ("Sealed limits follow approve, edit, seal and enforce", TestLimitsSealStateMachine),
    ("Sealed limits are restored in the database", TestSealedLimitsRestore),
    ("Parent lock sets, extends, clears and expires", TestParentLock),
    ("Weekly report compares this week with last week", TestWeeklyReport),
    ("Bedtime signs out overnight and honours the weekend bedtime", TestBedtime),
    ("Time requests are de-duplicated, expire and resolve", TestTimeRequests),
    ("Limits guard restores outside edits but accepts its own", TestLimitsGuard),
    ("Repeated closes alert once at the threshold", TestRepeatedCloseDetector),
    ("Remote PIN lockouts are counted and the PIN folder check fails closed", TestRemotePinLockoutCounterAndFolderCheck),
    ("Hotkey registered flag clears on unregister", TestHotKeyRegisteredFlagClears),
    ("Screen time remote route needs an admin login and same origin", TestScreenTimeRemoteRouteBoundary),
    ("Screen time limits pending approvals per caller", TestScreenTimeApprovalCap),
    ("Screen time protocol preserves selected account payload", TestScreenTimeProtocol),
    ("Screen time protocol rejects oversized or invalid frames", TestScreenTimeProtocolBounds),
    ("Screen time approval requires elevation and prevents replay", TestScreenTimeAuthorization),
    ("Screen time approval expires and respects caller identity", TestScreenTimeAuthorizationRequiresCredentials),
    ("Screen time checkpoints replace atomically with backup", TestScreenTimeAtomicFiles),
    ("Screen time isolates accounts and grants daily bonus", TestScreenTimeAccountIsolation),
    ("Screen time survives restart and clock rollback", TestScreenTimeDayAndRestart),
    ("Screen time rejects invalid policies", TestScreenTimePolicyValidation),
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
    ("Usage database tracks email commands per device", TestUsageDatabaseTracksEmailCommands),
    ("Defaults are imported only on first database creation", TestDefaultsImportedOnlyOnce),
    ("Existing empty databases do not reimport defaults", TestExistingDatabaseDoesNotReimportDefaults),
    ("Usage database replaces backup-managed config tables", TestUsageDatabaseReplacesConfigTables),
    ("Window tracker returns every process mapped to an app", TestWindowTrackerReturnsAllAppProcesses),
    ("Window tracker validates idle tracking settings", TestWindowTrackerIdleSettings),
    ("Steam library folders are parsed from libraryfolders.vdf", TestSteamLibraryVdfParsing),
    ("Daily summaries calculate latest missed delivery", TestDailySummaryDueTime),
    ("Weekly summaries calculate latest missed delivery", TestWeeklySummaryDueTime),
    ("Monthly summaries clamp delivery days", TestMonthlySummaryDueTime),
    ("Watchdog restart tamper delivery is retry-safe", TestWatchdogRestartTamperDelivery),
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

static Task TestSteamLibraryVdfParsing()
{
    var vdf = "\"libraryfolders\"\n{\n" +
              "\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n\t}\n" +
              "\t\"1\"\n\t{\n\t\t\"path\"\t\t\"D:\\\\SteamLibrary\"\n\t}\n" +
              "\t\"2\"\n\t{\n\t\t\"path\"\t\t\"E:\\\\Games\\\\Steam\"\n\t}\n}\n";

    var paths = DiscoveryService.ParseSteamLibraryPaths(vdf).ToList();

    AssertEqual(3, paths.Count, "Expected three library paths.");
    AssertTrue(paths.Contains(@"C:\Program Files (x86)\Steam"), "Expected the default Steam path (backslashes unescaped).");
    AssertTrue(paths.Contains(@"D:\SteamLibrary"), "Expected the D: library path.");
    AssertTrue(paths.Contains(@"E:\Games\Steam"), "Expected the E: library path.");
    AssertEqual(0, DiscoveryService.ParseSteamLibraryPaths("").Count(), "Empty content yields no paths.");

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

static async Task TestWatchdogRestartTamperDelivery()
{
    using var db = CreateTempDatabase();
    using var tracker = new WindowTracker();
    var enforcer = new LimitEnforcer(db, tracker);
    var scheduler = new SchedulerService(db);
    using var email = new EmailService(db, tracker, enforcer, scheduler);
    using var notifications = new NotificationService(db);
    using var reports = new ParentReportService(db, email, notifications, "missing-appsettings.json");

    var disabledResult = await reports.ReportWatchdogRestartAsync("test marker");
    AssertTrue(disabledResult, "A disabled tamper channel should consume the marker without retrying forever.");

    await db.SetSettingAsync("TamperAlertsEnabled", "true");
    var failedDelivery = await reports.ReportWatchdogRestartAsync("test marker");
    AssertFalse(failedDelivery, "A configured tamper event with no delivery channel should retain the marker for retry.");
}

static async Task TestUsageDatabaseTracksEmailCommands()
{
    using var db = CreateTempDatabase();
    AssertFalse(await db.IsEmailCommandProcessedAsync("message-1"), "A new command must not be processed.");
    await db.MarkEmailCommandProcessedAsync("message-1", DateTimeOffset.UtcNow.AddDays(-100));
    AssertTrue(await db.IsEmailCommandProcessedAsync("message-1"), "The command receipt must persist.");
    await db.MarkEmailCommandProcessedAsync("message-1");
    await db.DeleteProcessedEmailCommandsBeforeAsync(DateTimeOffset.UtcNow.AddDays(-90));
    AssertFalse(await db.IsEmailCommandProcessedAsync("message-1"), "Old command receipts must be pruned.");
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

static Task TestScreenTimeAccountIsolation()
{
    var a = new MonitorAndControl.ScreenTime.ScreenTimeUsage();
    var b = new MonitorAndControl.ScreenTime.ScreenTimeUsage();
    var day = new DateOnly(2026, 10, 25);
    var policy = new MonitorAndControl.ScreenTime.ScreenTimePolicy {
        UserSid = "S-1-5-21-1-2-3-1001", DisplayName = "Account A", DailyMinutes = 180
    };
    MonitorAndControl.ScreenTime.ScreenTimeRules.Advance(a, day, 5);
    AssertTrue(a.Seconds == 5 && b.Seconds == 0, "Usage must be isolated per profile.");
    AssertTrue(MonitorAndControl.ScreenTime.ScreenTimeRules.Remaining(policy, a, day) == 10795, "Three hours is 10800 seconds.");
    policy.BonusDay = day.ToString("yyyy-MM-dd"); policy.BonusMinutes = 15;
    AssertTrue(MonitorAndControl.ScreenTime.ScreenTimeRules.Remaining(policy, a, day) == 11695, "Bonus applies today.");
    return Task.CompletedTask;
}

static Task TestScreenTimeDayAndRestart()
{
    var day = new DateOnly(2026, 10, 25);
    var usage = new MonitorAndControl.ScreenTime.ScreenTimeUsage { Day = "2026-10-25", Seconds = 10800 };
    var policy = new MonitorAndControl.ScreenTime.ScreenTimePolicy {
        UserSid = "S-1-5-21-1-2-3-1001", DisplayName = "Account A", DailyMinutes = 180
    };
    var json = System.Text.Json.JsonSerializer.Serialize(usage);
    usage = System.Text.Json.JsonSerializer.Deserialize<MonitorAndControl.ScreenTime.ScreenTimeUsage>(json)!;
    AssertTrue(MonitorAndControl.ScreenTime.ScreenTimeRules.Remaining(policy, usage, day) == 0, "Restart preserves quota exhaustion.");
    MonitorAndControl.ScreenTime.ScreenTimeRules.Advance(usage, day.AddDays(-1), 1);
    AssertTrue(usage.Seconds == 10801, "Clock rollback must not reset usage.");
    MonitorAndControl.ScreenTime.ScreenTimeRules.Advance(usage, day.AddDays(1), 1);
    AssertTrue(usage.Seconds == 1 && usage.Day == "2026-10-26", "New day renews once.");
    policy.BonusDay = "2026-10-25"; policy.BonusMinutes = 20;
    AssertTrue(MonitorAndControl.ScreenTime.ScreenTimeRules.Remaining(policy, usage, day.AddDays(1)) == 10799, "Old bonus expires.");
    return Task.CompletedTask;
}

static Task TestScreenTimePolicyValidation()
{
    var policy = new MonitorAndControl.ScreenTime.ScreenTimePolicy {
        UserSid = "S-1-5-21-1-2-3-1001", DisplayName = "Account A", DailyMinutes = 0
    };
    bool rejected = false;
    try { policy.Validate(); } catch (InvalidDataException) { rejected = true; }
    AssertTrue(rejected, "Zero-minute active policy must not be accepted.");
    return Task.CompletedTask;
}

static Task TestScreenTimeAtomicFiles()
{
    var directory = Path.Combine(Path.GetTempPath(), "DeviceMon-ScreenTime-Test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "usage.json");
    try
    {
        MonitorAndControl.ScreenTime.ScreenTimeFiles.WriteAtomic(path, new { Seconds = 123 });
        MonitorAndControl.ScreenTime.ScreenTimeFiles.WriteAtomic(path, new { Seconds = 456 });
        using var current = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        using var backup = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path + ".bak"));
        AssertTrue(current.RootElement.GetProperty("Seconds").GetInt32() == 456, "New checkpoint committed.");
        AssertTrue(backup.RootElement.GetProperty("Seconds").GetInt32() == 123, "Previous checkpoint recoverable.");
        AssertTrue(Directory.GetFiles(directory, "*.tmp").Length == 0, "No temporary checkpoint left.");
    }
    finally { Directory.Delete(directory, true); }
    return Task.CompletedTask;
}

static async Task TestScreenTimeProtocol()
{
    using var stream = new MemoryStream();
    var request = new MonitorAndControl.ScreenTime.ScreenTimeRequest {
        Command = "save", UserSid = "S-1-5-21-1-2-3-1001", DailyMinutes = 180, BonusMinutes = 10, Enabled = true,
        TicketId = "test-ticket"
    };
    await MonitorAndControl.ScreenTime.ScreenTimeProtocol.WriteAsync(stream, request, CancellationToken.None);
    stream.Position = 0;
    var copy = await MonitorAndControl.ScreenTime.ScreenTimeProtocol.ReadAsync<MonitorAndControl.ScreenTime.ScreenTimeRequest>(stream, 16384, CancellationToken.None);
    AssertTrue(copy.UserSid == request.UserSid && copy.DailyMinutes == 180 && copy.BonusMinutes == 10, "Selected account settings must round-trip.");
    AssertTrue(copy.TicketId == request.TicketId, "Authorization must reach service through bounded protocol.");
}

static async Task TestScreenTimeProtocolBounds()
{
    foreach (var length in new[] { -1, 0, 16385 })
    {
        using var stream = new MemoryStream(BitConverter.GetBytes(length));
        bool rejected = false;
        try { await MonitorAndControl.ScreenTime.ScreenTimeProtocol.ReadAsync<MonitorAndControl.ScreenTime.ScreenTimeRequest>(stream, 16384, CancellationToken.None); }
        catch (InvalidDataException) { rejected = true; }
        AssertTrue(rejected, "Invalid message length must be rejected before allocation.");
    }
}

static Task TestHotKeyErrorDescriptions()
{
    AssertTrue(HotKeyService.DescribeError(1409).Contains("already in use"), "Error 1409 means another program owns the hotkey.");
    AssertTrue(HotKeyService.DescribeError(87).Contains("not valid"), "Error 87 means an invalid combination.");
    AssertTrue(HotKeyService.DescribeError(12345).Contains("12345"), "Unknown errors still show their code.");
    AssertFalse(HotKeyService.DescribeError(1409).Contains("Control"), "Descriptions must not reveal the combination.");
    return Task.CompletedTask;
}

static Task TestUpdateStatusCasing()
{
    var directory = Path.Combine(Path.GetTempPath(), "DeviceMon-Diag-Test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        // UpdateAgent writes PascalCase; the dashboard itself writes camelCase.
        File.WriteAllText(Path.Combine(directory, "update-status.json"),
            "{ \"Status\": \"success\", \"FinishedAt\": \"2026-11-01T10:00:00+00:00\", \"Message\": \"Update completed successfully.\" }");
        var pascal = DiagnosticsService.ReadUpdateStatus(directory);
        AssertEqual("success", pascal.Status, "PascalCase status must be recognised.");
        AssertEqual("Update completed successfully.", pascal.Message, "PascalCase message must be recognised.");

        File.WriteAllText(Path.Combine(directory, "update-status.json"), "{ \"status\": \"failed\", \"message\": \"boom\" }");
        AssertEqual("failed", DiagnosticsService.ReadUpdateStatus(directory).Status, "camelCase status must be recognised.");

        File.WriteAllText(Path.Combine(directory, "update-status.json"), "{ not json");
        AssertEqual("unreadable", DiagnosticsService.ReadUpdateStatus(directory).Status, "Corrupt status is reported, not thrown.");
        File.Delete(Path.Combine(directory, "update-status.json"));
        AssertEqual("none", DiagnosticsService.ReadUpdateStatus(directory).Status, "No file means no update recorded.");
    }
    finally { Directory.Delete(directory, true); }
    return Task.CompletedTask;
}

static Task TestRemotePinVerifyAndStorage()
{
    var directory = Path.Combine(Path.GetTempPath(), "DeviceMon-Pin-Test-" + Guid.NewGuid().ToString("N"));
    var path = Path.Combine(directory, "remote-pin.json");
    try
    {
        var store = new MonitorAndControl.ScreenTime.RemotePinStore(path);
        var now = DateTimeOffset.UtcNow;
        AssertTrue(store.Verify("anything", now).Status == MonitorAndControl.ScreenTime.RemotePinStatus.NotConfigured, "No PIN set means remote changes are off.");

        store.Set("correct horse 42");
        var text = File.ReadAllText(path);
        AssertFalse(text.Contains("correct horse"), "The PIN must never be stored in plain text.");
        AssertTrue(text.Contains("Hash") && text.Contains("Salt"), "A salted hash is stored.");

        var wrong = store.Verify("wrong pin", now);
        AssertTrue(wrong.Status == MonitorAndControl.ScreenTime.RemotePinStatus.Wrong && wrong.Message.Contains("4 attempt"), "A wrong PIN is refused and counted.");
        AssertTrue(store.Verify("", now).Status == MonitorAndControl.ScreenTime.RemotePinStatus.Wrong, "An empty PIN counts as a wrong guess.");
        AssertTrue(store.Verify("x" + new string('y', 200), now).Status == MonitorAndControl.ScreenTime.RemotePinStatus.Wrong, "An oversized PIN is refused without hashing it.");

        // Failures survive a restart of the service (a new store instance reads the same file).
        var reopened = new MonitorAndControl.ScreenTime.RemotePinStore(path);
        AssertTrue(reopened.Verify("nope", now).Message.Contains("1 attempt"), "Failed attempts are persisted.");
        AssertTrue(reopened.Verify("correct horse 42", now).Status == MonitorAndControl.ScreenTime.RemotePinStatus.Ok, "The right PIN works.");
        AssertTrue(reopened.Verify("wrong", now).Message.Contains("4 attempt"), "A successful login resets the failure count.");

        store.Clear();
        AssertFalse(File.Exists(path) || File.Exists(path + ".bak"), "Clearing removes the PIN and its backup copy.");
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    return Task.CompletedTask;
}

static Task TestRemotePinLockout()
{
    var directory = Path.Combine(Path.GetTempPath(), "DeviceMon-Pin-Test-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new MonitorAndControl.ScreenTime.RemotePinStore(Path.Combine(directory, "remote-pin.json"));
        store.Set("correct horse 42");
        var now = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

        MonitorAndControl.ScreenTime.RemotePinResult last = null!;
        for (var i = 0; i < 5; i++) last = store.Verify("guess" + i, now);
        AssertTrue(last.Status == MonitorAndControl.ScreenTime.RemotePinStatus.Locked && last.LockedUntil == now.AddMinutes(15), "Five wrong guesses lock for 15 minutes.");

        var duringLock = store.Verify("correct horse 42", now.AddMinutes(14));
        AssertTrue(duringLock.Status == MonitorAndControl.ScreenTime.RemotePinStatus.Locked, "Even the right PIN is refused while locked.");
        AssertTrue(store.GetStatus(now.AddMinutes(1)).LockedUntil == now.AddMinutes(15), "The lock is visible to the dashboard.");

        // After the lock expires, a second round of failures locks for twice as long.
        var later = now.AddMinutes(16);
        for (var i = 0; i < 5; i++) last = store.Verify("guess" + i, later);
        AssertTrue(last.LockedUntil == later.AddMinutes(30), "The second lockout is 30 minutes.");

        var third = later.AddMinutes(31);
        AssertTrue(store.Verify("correct horse 42", third).Status == MonitorAndControl.ScreenTime.RemotePinStatus.Ok, "The right PIN works once the lock has expired.");
        for (var i = 0; i < 5; i++) last = store.Verify("guess" + i, third);
        AssertTrue(last.LockedUntil == third.AddMinutes(15), "A successful login resets the lockout length.");
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    return Task.CompletedTask;
}

static Task TestRemotePinRules()
{
    foreach (var bad in new[] { "", "12345", "aaaaaa", "ababab", new string('x', 65) })
        AssertFalse(MonitorAndControl.ScreenTime.RemotePinStore.IsAcceptable(bad, out _), $"'{bad}' must be rejected.");
    AssertTrue(MonitorAndControl.ScreenTime.RemotePinStore.IsAcceptable("483920", out _), "A six-digit PIN with enough variety is accepted.");
    var store = new MonitorAndControl.ScreenTime.RemotePinStore(Path.Combine(Path.GetTempPath(), "never-created-" + Guid.NewGuid().ToString("N"), "pin.json"));
    var refused = false;
    try { store.Set("123"); } catch (ArgumentException) { refused = true; }
    AssertTrue(refused, "Setting a weak PIN throws.");
    return Task.CompletedTask;
}

static async Task TestScreenTimeRemoteRouteBoundary()
{
    async Task<Microsoft.AspNetCore.Builder.WebApplication> StartAsync(Func<Microsoft.AspNetCore.Http.HttpContext, bool> admin)
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(
            new Microsoft.AspNetCore.Builder.WebApplicationOptions { EnvironmentName = "Testing", ContentRootPath = Path.GetTempPath() });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        MonitorAndControl.Web.ScreenTimeEndpoints.Map(app, admin);
        await app.StartAsync();
        return app;
    }

    StringContent Json(string body) => new(body, System.Text.Encoding.UTF8, "application/json");

    // No admin login: refused before anything touches the service.
    await using (var anonymous = await StartAsync(_ => false))
    {
        using var client = new HttpClient { BaseAddress = new Uri(anonymous.Urls.Single()) };
        using var denied = await client.PostAsync("/api/screen-time/remote-policy", Json("{\"pin\":\"x\"}"));
        AssertTrue(denied.StatusCode == System.Net.HttpStatusCode.Forbidden, "Remote changes need an admin login.");
        using var readDenied = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/screen-time") { Headers = { Host = "example.invalid" } });
        AssertTrue(readDenied.StatusCode == System.Net.HttpStatusCode.Forbidden, "A remote read also needs an admin login.");
    }

    // Logged in: bad input is still refused before reaching the service.
    await using var admin = await StartAsync(_ => true);
    using var http = new HttpClient { BaseAddress = new Uri(admin.Urls.Single()) };
    using var foreign = new HttpRequestMessage(HttpMethod.Post, "/api/screen-time/remote-policy") { Content = Json("{\"pin\":\"x\"}") };
    foreign.Headers.Add("Origin", "https://example.invalid");
    using var crossSite = await http.SendAsync(foreign);
    AssertTrue(crossSite.StatusCode == System.Net.HttpStatusCode.Forbidden, "A cross-site request is refused even when logged in.");
    using var noPin = await http.PostAsync("/api/screen-time/remote-policy", Json("{\"userSid\":\"S-1-5-21-1-2-3-1001\",\"dailyMinutes\":60}"));
    AssertTrue(noPin.StatusCode == System.Net.HttpStatusCode.BadRequest, "A missing PIN is refused.");
    using var badJson = await http.PostAsync("/api/screen-time/remote-policy", Json("{"));
    AssertTrue(badJson.StatusCode == System.Net.HttpStatusCode.BadRequest, "Malformed JSON is refused.");
    using var tooBig = await http.PostAsync("/api/screen-time/remote-policy", Json(new string(' ', 16385)));
    AssertTrue(tooBig.StatusCode == System.Net.HttpStatusCode.BadRequest, "An oversized body is refused.");
}

static Task TestRemotePinLockoutCounterAndFolderCheck()
{
    var directory = Path.Combine(Path.GetTempPath(), "DeviceMon-Pin-Test-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new MonitorAndControl.ScreenTime.RemotePinStore(Path.Combine(directory, "remote-pin.json"));
        store.Set("correct horse 42");
        var now = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        AssertEqual(0, store.GetStatus(now).Lockouts, "No lockouts yet.");

        for (var i = 0; i < 5; i++) store.Verify("guess" + i, now);
        AssertEqual(1, store.GetStatus(now).Lockouts, "A lockout is counted so the host can alert on it.");

        // A correct PIN later must not erase the evidence that someone was guessing.
        store.Verify("correct horse 42", now.AddMinutes(16));
        AssertEqual(1, store.GetStatus(now.AddMinutes(16)).Lockouts, "The lockout counter is never reset by a correct PIN.");
        for (var i = 0; i < 5; i++) store.Verify("guess" + i, now.AddMinutes(17));
        AssertEqual(2, store.GetStatus(now.AddMinutes(17)).Lockouts, "Each new lockout increments the counter.");

        // The PIN is only trusted inside a folder closed to the monitored user. An ordinary folder under
        // the user's own profile is owned and readable by that user, so it must be rejected (fail closed).
        AssertFalse(MonitorAndControl.ScreenTime.DirectoryProtection.IsProtectedFromUsers(directory), "A user-owned folder is not protected.");
        AssertFalse(MonitorAndControl.ScreenTime.DirectoryProtection.IsProtectedFromUsers(Path.Combine(directory, "missing")), "A missing folder is not protected.");
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    return Task.CompletedTask;
}

static Task TestHotKeyRegisteredFlagClears()
{
    // Unregistering must clear the flag the diagnostics read, even when nothing was registered.
    HotKeyService.Unregister(IntPtr.Zero, 1);
    HotKeyService.Unregister();
    AssertFalse(HotKeyService.Registered, "The hotkey is reported as not registered after Unregister.");
    return Task.CompletedTask;
}

static Task TestScreenTimeApprovalCap()
{
    var gate = new MonitorAndControl.ScreenTime.ScreenTimeApprovals();
    var now = DateTimeOffset.UtcNow;
    var change = new MonitorAndControl.ScreenTime.ScreenTimeChange("a", 180, 0, true);
    for (var i = 0; i < 4; i++) gate.Create("child", change, now);
    var refused = false;
    try { gate.Create("child", change, now); } catch (InvalidOperationException) { refused = true; }
    AssertTrue(refused, "A single caller cannot fill the pending-approval queue.");
    AssertTrue(gate.Create("parent", change, now).Length == 32, "Other callers are unaffected by one caller's backlog.");
    return Task.CompletedTask;
}

static Task TestScreenTimeAuthorization()
{
    var gate = new MonitorAndControl.ScreenTime.ScreenTimeApprovals();
    var now = DateTimeOffset.UtcNow;
    var change = new MonitorAndControl.ScreenTime.ScreenTimeChange("account-a", 180, 0, true);
    var id = gate.Create("caller", change, now);
    AssertFalse(gate.TryClaim(id, false, now, out _), "Standard callers cannot approve.");
    AssertTrue(gate.TryClaim(id, true, now, out var frozen) && frozen == change, "Elevated approval preserves target.");
    AssertFalse(gate.TryClaim(id, true, now, out _), "Approval cannot be replayed.");
    gate.Complete(id, MonitorAndControl.ScreenTime.ScreenTimeReply.Success(new { saved = true }));
    AssertFalse(gate.Result(id, "another", now).Ok, "Other callers cannot read results.");
    AssertTrue(gate.Result(id, "caller", now).Ok, "Owner receives confirmation.");
    return Task.CompletedTask;
}
static Task TestScreenTimeAuthorizationRequiresCredentials()
{
    var gate = new MonitorAndControl.ScreenTime.ScreenTimeApprovals();
    var now = DateTimeOffset.UtcNow;
    var change = new MonitorAndControl.ScreenTime.ScreenTimeChange("a", 180, 0, true);
    var expired = gate.Create("caller", change, now);
    AssertFalse(gate.TryClaim(expired, true, now.AddMinutes(3), out _), "Expired approval refused.");
    var canceled = gate.Create("caller", change, now);
    gate.Cancel(canceled, "other");
    AssertTrue(gate.TryClaim(canceled, true, now, out _), "Other caller cannot cancel.");
    var owned = gate.Create("caller", change, now);
    gate.Cancel(owned, "caller");
    AssertFalse(gate.TryClaim(owned, true, now, out _), "Canceled approval refused.");
    return Task.CompletedTask;
}

static async Task TestScreenTimeHttpBoundary()
{
    var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(
        new Microsoft.AspNetCore.Builder.WebApplicationOptions { EnvironmentName = "Testing", ContentRootPath = Path.GetTempPath() });
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    await using var app = builder.Build();
    MonitorAndControl.Web.ScreenTimeEndpoints.Map(app);
    await app.StartAsync();
    try
    {
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var foreignOrigin = new HttpRequestMessage(HttpMethod.Post, "/api/screen-time/policy");
        foreignOrigin.Headers.Add("Origin", "https://example.invalid");
        foreignOrigin.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        using var forbidden = await client.SendAsync(foreignOrigin);
        AssertTrue(forbidden.StatusCode == System.Net.HttpStatusCode.Forbidden, "Foreign-origin changes must be refused before service access.");
        using var foreignHost = new HttpRequestMessage(HttpMethod.Get, "/api/screen-time");
        foreignHost.Headers.Host = "example.invalid";
        using var rejectedHost = await client.SendAsync(foreignHost);
        AssertTrue(rejectedHost.StatusCode == System.Net.HttpStatusCode.Forbidden, "DNS-rebinding host must be refused.");
        using var badJson = await client.PostAsync("/api/screen-time/policy", new StringContent("{", System.Text.Encoding.UTF8, "application/json"));
        AssertTrue(badJson.StatusCode == System.Net.HttpStatusCode.BadRequest, "Malformed JSON must be refused before service access.");
        using var large = await client.PostAsync("/api/screen-time/policy", new StringContent(new string(' ', 16385), System.Text.Encoding.UTF8, "application/json"));
        AssertTrue(large.StatusCode == System.Net.HttpStatusCode.BadRequest, "Oversized body must be refused before service access.");
    }
    finally { await app.StopAsync(); }
}

static Task TestWeekendLimit()
{
    var policy = new MonitorAndControl.ScreenTime.ScreenTimePolicy
    {
        UserSid = "S-1-5-21-1-2-3-1001", DisplayName = "kid", DailyMinutes = 60, WeekendMinutes = 120
    };
    var saturday = new DateOnly(2026, 11, 7);
    var monday = new DateOnly(2026, 11, 9);
    AssertTrue(MonitorAndControl.ScreenTime.ScreenTimeRules.LimitFor(policy, saturday) == 120, "Saturday uses the weekend minutes.");
    AssertTrue(MonitorAndControl.ScreenTime.ScreenTimeRules.LimitFor(policy, saturday.AddDays(1)) == 120, "Sunday uses the weekend minutes.");
    AssertTrue(MonitorAndControl.ScreenTime.ScreenTimeRules.LimitFor(policy, monday) == 60, "A weekday uses the daily minutes.");
    policy.WeekendMinutes = 0;
    AssertTrue(MonitorAndControl.ScreenTime.ScreenTimeRules.LimitFor(policy, saturday) == 60, "Zero weekend minutes means the same limit every day.");
    var usage = new MonitorAndControl.ScreenTime.ScreenTimeUsage { Day = "2026-11-07", Seconds = 600 };
    policy.WeekendMinutes = 120;
    AssertTrue(MonitorAndControl.ScreenTime.ScreenTimeRules.Remaining(policy, usage, saturday) == 120 * 60 - 600, "Remaining uses the weekend limit.");
    policy.WeekendMinutes = 2000;
    var refused = false;
    try { policy.Validate(); } catch (InvalidDataException) { refused = true; }
    AssertTrue(refused, "An out-of-range weekend limit is rejected.");
    return Task.CompletedTask;
}

static Task TestRepeatedCloseDetector()
{
    var now = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    var window = TimeSpan.FromMinutes(30);
    var history = new List<DateTimeOffset> { now.AddMinutes(-90), now.AddMinutes(-20), now.AddMinutes(-10) };
    AssertTrue(MonitorAndControl.Services.RepeatedCloseDetector.CountWithin(history, now, window) == 2, "Only restarts inside the window count.");
    AssertFalse(MonitorAndControl.Services.RepeatedCloseDetector.IsFirstAtThreshold(history, now, window, 3), "Two recent closes do not reach three.");
    history.Add(now);
    AssertTrue(MonitorAndControl.Services.RepeatedCloseDetector.IsFirstAtThreshold(history, now, window, 3), "The third close inside the window alerts.");
    history.Add(now.AddMinutes(1));
    AssertFalse(MonitorAndControl.Services.RepeatedCloseDetector.IsFirstAtThreshold(history, now.AddMinutes(1), window, 3), "The fourth close does not alert again.");
    var round = MonitorAndControl.Services.RepeatedCloseDetector.Parse(MonitorAndControl.Services.RepeatedCloseDetector.Serialize(history, now.AddMinutes(1)));
    AssertTrue(round.Count == 5, "History survives a save and load.");
    AssertTrue(MonitorAndControl.Services.RepeatedCloseDetector.Parse("garbage;;").Count == 0, "Corrupt history is ignored.");
    return Task.CompletedTask;
}

static async Task TestLimitsGuard()
{
    var dir = Path.Combine(Path.GetTempPath(), "DeviceMon-Guard-Test-" + Guid.NewGuid().ToString("N"));
    var path = Path.Combine(dir, "monitor.db");
    try
    {
        var db = new MonitorAndControl.Data.UsageDatabase(path);
        await db.SaveLimitRuleAsync(new MonitorAndControl.Models.AppLimitRule { AppName = "Game", DailyMaxMinutes = 60, Enabled = true });
        AssertTrue(await db.CheckLimitsIntegrityAsync() == null, "The first check only records a baseline.");

        await db.SaveLimitRuleAsync(new MonitorAndControl.Models.AppLimitRule { AppName = "Game", DailyMaxMinutes = 90, Enabled = true });
        AssertTrue(await db.CheckLimitsIntegrityAsync() == null, "An edit made through the app is accepted.");

        // Another connection (the child using a database tool) lifts the limit.
        using (var outside = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path))
        {
            outside.Open();
            using var cmd = outside.CreateCommand();
            cmd.CommandText = "UPDATE AppLimits SET DailyMaxMinutes = 1440, Enabled = 0";
            cmd.ExecuteNonQuery();
        }
        var problem = await db.CheckLimitsIntegrityAsync();
        AssertTrue(problem != null && problem.Contains("restored"), "An outside edit is reported.");
        var rules = await db.GetLimitRulesAsync();
        AssertTrue(rules.Count == 1 && rules[0].DailyMaxMinutes == 90 && rules[0].Enabled, "The original limit was put back.");
        AssertTrue(await db.CheckLimitsIntegrityAsync() == null, "After restoring, the guard is quiet again.");

        // Writes to unrelated tables by another connection are not a limits change.
        using (var outside = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path))
        {
            outside.Open();
            using var cmd = outside.CreateCommand();
            cmd.CommandText = "INSERT INTO Settings (Key, Value) VALUES ('x', 'y') ON CONFLICT(Key) DO UPDATE SET Value='y'";
            cmd.ExecuteNonQuery();
        }
        AssertTrue(await db.CheckLimitsIntegrityAsync() == null, "An outside write that leaves limits alone is not flagged.");
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }
}

static Task TestBedtime()
{
    var p = new MonitorAndControl.ScreenTime.ScreenTimePolicy
    {
        UserSid = "S-1-5-21-1-2-3-1001", DisplayName = "kid", BedtimeStart = "21:00", BedtimeEnd = "07:00", WeekendBedtimeStart = "23:00"
    };
    p.Validate();
    // 2026-11-09 is a Monday.
    bool In(string day, int h, int m) => MonitorAndControl.ScreenTime.ScreenTimeRules.InBedtime(p, DateTime.Parse(day + $" {h:00}:{m:00}"));
    AssertFalse(In("2026-11-09", 12, 0), "Midday on a school day is allowed.");
    AssertTrue(In("2026-11-09", 21, 0), "Bedtime starts on the minute.");
    AssertTrue(In("2026-11-09", 23, 59), "Late evening is bedtime.");
    AssertTrue(In("2026-11-10", 6, 59), "The early morning after is still bedtime.");
    AssertFalse(In("2026-11-10", 7, 0), "Wake-up time ends bedtime.");
    // Friday 2026-11-13: the night uses the 23:00 weekend bedtime; Saturday morning runs to 07:00.
    AssertFalse(In("2026-11-13", 22, 0), "Friday 22:00 is before the weekend bedtime.");
    AssertTrue(In("2026-11-13", 23, 0), "Friday 23:00 is the weekend bedtime.");
    AssertTrue(In("2026-11-14", 6, 0), "Saturday early morning is still the Friday night.");
    AssertFalse(In("2026-11-14", 22, 0), "Saturday 22:00 is before the weekend bedtime.");
    AssertTrue(In("2026-11-15", 21, 30), "Sunday night uses the normal 21:00 bedtime.");

    AssertTrue(MonitorAndControl.ScreenTime.ScreenTimeRules.MinutesUntilBedtime(p, DateTime.Parse("2026-11-09 20:51")) == 9, "Nine minutes before bedtime.");
    AssertTrue(MonitorAndControl.ScreenTime.ScreenTimeRules.MinutesUntilBedtime(p, DateTime.Parse("2026-11-09 22:00")) == null, "No countdown during bedtime.");

    var bad = new MonitorAndControl.ScreenTime.ScreenTimePolicy { UserSid = "S-1-5-21-1-2-3-1001", DisplayName = "kid", BedtimeStart = "21:00" };
    var refused = false;
    try { bad.Validate(); } catch (InvalidDataException) { refused = true; }
    AssertTrue(refused, "A bedtime without a wake-up time is rejected.");
    bad.BedtimeEnd = "25:00";
    refused = false;
    try { bad.Validate(); } catch (InvalidDataException) { refused = true; }
    AssertTrue(refused, "An impossible clock time is rejected.");
    var none = new MonitorAndControl.ScreenTime.ScreenTimePolicy { UserSid = "S-1-5-21-1-2-3-1001", DisplayName = "kid" };
    none.Validate();
    AssertFalse(MonitorAndControl.ScreenTime.ScreenTimeRules.InBedtime(none, DateTime.Parse("2026-11-09 23:00")), "No bedtime configured means never bedtime.");
    return Task.CompletedTask;
}

static Task TestTimeRequests()
{
    var directory = Path.Combine(Path.GetTempPath(), "DeviceMon-Req-Test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "time-requests.json");
    try
    {
        var now = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        AssertTrue(MonitorAndControl.ScreenTime.TimeRequestStore.Add("S-1", "kid", now, path), "The first request is recorded.");
        AssertFalse(MonitorAndControl.ScreenTime.TimeRequestStore.Add("S-1", "kid", now.AddMinutes(1), path), "A second request while one waits is ignored.");
        AssertTrue(MonitorAndControl.ScreenTime.TimeRequestStore.Add("S-2", "sibling", now, path), "Other accounts have their own request.");
        AssertTrue(MonitorAndControl.ScreenTime.TimeRequestStore.Pending(now.AddMinutes(5), path).Count == 2, "Both are waiting.");
        AssertTrue(MonitorAndControl.ScreenTime.TimeRequestStore.Pending(now.AddHours(13), path).Count == 0, "Old requests expire.");
        AssertTrue(MonitorAndControl.ScreenTime.TimeRequestStore.Resolve("S-1", path) == 1, "Approving closes the request.");
        AssertTrue(MonitorAndControl.ScreenTime.TimeRequestStore.Pending(now.AddMinutes(5), path).Single().UserSid == "S-2", "Only the other account still waits.");
        AssertTrue(MonitorAndControl.ScreenTime.TimeRequestStore.Add("S-1", "kid", now.AddMinutes(10), path), "After an answer the child may ask again.");
        File.WriteAllText(path, "not json");
        AssertTrue(MonitorAndControl.ScreenTime.TimeRequestStore.Pending(now, path).Count == 0, "A corrupt file is treated as empty.");
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    return Task.CompletedTask;
}

static Task TestWeeklyReport()
{
    var end = new DateTime(2026, 11, 15);
    MonitorAndControl.Models.AppUsageRecord Rec(string app, DateTime day, long seconds) =>
        new() { AppName = app, Date = day, TotalSeconds = seconds };
    var records = new List<MonitorAndControl.Models.AppUsageRecord>
    {
        Rec("Game", end, 3600), Rec("Game", end.AddDays(-1), 1800), Rec("Video", end.AddDays(-2), 600),
        Rec("Game", end.AddDays(-7), 1800), Rec("Game", end.AddDays(-8), 1800), Rec("Chat", end.AddDays(-9), 600),
        Rec("Old", end.AddDays(-20), 9999), Rec("Future", end.AddDays(1), 9999)
    };
    var report = MonitorAndControl.Services.WeeklyReport.Build(records, end);
    AssertTrue(report.From == "2026-11-09" && report.To == "2026-11-15", "This week is the 7 days ending today.");
    AssertTrue(report.PreviousFrom == "2026-11-02" && report.PreviousTo == "2026-11-08", "Last week is the 7 days before that.");
    AssertTrue(report.ThisWeekSeconds == 6000 && report.LastWeekSeconds == 4200, "Totals ignore anything outside the two weeks.");
    AssertTrue(report.ChangePercent == 43, "The change is rounded to whole percent.");
    AssertTrue(report.Days.Count == 7 && report.Days[6].ThisWeekSeconds == 3600 && report.Days[6].LastWeekSeconds == 1800, "Each day is lined up with the same day a week earlier.");
    AssertTrue(report.Apps[0].App == "Game" && report.Apps[0].ThisWeekSeconds == 5400 && report.Apps[0].LastWeekSeconds == 3600, "Apps are ranked by this week's use.");
    AssertTrue(report.Apps.Any(a => a.App == "Chat" && a.ThisWeekSeconds == 0), "An app used only last week still appears.");
    AssertFalse(report.Apps.Any(a => a.App is "Old" or "Future"), "Out-of-range records are ignored.");
    var overlapping = new List<MonitorAndControl.Models.AppUsageRecord>
    {
        new() { AppName = "Game", Date = end, TotalSeconds = 3600, ForegroundSeconds = 1800, BackgroundSeconds = 1800 },
        new() { AppName = "Music", Date = end, TotalSeconds = 3600, ForegroundSeconds = 0, BackgroundSeconds = 3600 }
    };
    AssertTrue(MonitorAndControl.Services.WeeklyReport.Build(overlapping, end).ThisWeekSeconds == 1800, "Background time that overlaps other apps is not added to the total.");
    AssertTrue(MonitorAndControl.Services.WeeklyReport.Build(new List<MonitorAndControl.Models.AppUsageRecord>(), end).ChangePercent == null, "No usage last week means no percentage.");
    AssertTrue(MonitorAndControl.Services.WeeklyReport.ToText(report).Contains("Game: 1h 30m vs 1h 0m (+50%)"), "The email text lists the app change.");
    return Task.CompletedTask;
}

static Task TestParentLock()
{
    var now = new DateTimeOffset(2026, 11, 9, 16, 0, 0, TimeSpan.Zero);
    var policy = new MonitorAndControl.ScreenTime.ScreenTimePolicy { UserSid = "S-1-5-21-1-2-3-1001", DisplayName = "kid" };
    AssertFalse(MonitorAndControl.ScreenTime.ScreenTimeRules.IsLocked(policy, now), "A new rule is not locked.");

    policy.LockUntilUtc = MonitorAndControl.ScreenTime.ScreenTimeRules.ApplyLock(policy.LockUntilUtc, 30, now);
    AssertTrue(policy.LockUntilUtc == now.AddMinutes(30), "Locking sets the end time.");
    AssertTrue(MonitorAndControl.ScreenTime.ScreenTimeRules.IsLocked(policy, now.AddMinutes(29)), "The account stays locked until the end time.");
    AssertFalse(MonitorAndControl.ScreenTime.ScreenTimeRules.IsLocked(policy, now.AddMinutes(30)), "The lock ends on time.");

    AssertTrue(MonitorAndControl.ScreenTime.ScreenTimeRules.ApplyLock(policy.LockUntilUtc, 0, now.AddMinutes(5)) == now.AddMinutes(30), "A normal save leaves the lock alone.");
    AssertTrue(MonitorAndControl.ScreenTime.ScreenTimeRules.ApplyLock(policy.LockUntilUtc, -1, now.AddMinutes(5)) == null, "Unlock clears it.");
    AssertTrue(MonitorAndControl.ScreenTime.ScreenTimeRules.ApplyLock(null, 99999, now) == now.AddMinutes(720), "A lock is capped at 12 hours.");
    var json = System.Text.Json.JsonSerializer.Serialize(policy);
    AssertTrue(System.Text.Json.JsonSerializer.Deserialize<MonitorAndControl.ScreenTime.ScreenTimePolicy>(json)!.LockUntilUtc == policy.LockUntilUtc, "The lock survives a save and load.");
    AssertTrue(System.Text.Json.JsonSerializer.Deserialize<MonitorAndControl.ScreenTime.ScreenTimePolicy>("{\"UserSid\":\"S-1-5-21-1-2-3-1001\",\"DisplayName\":\"kid\"}")!.LockUntilUtc == null, "Older rule files load as unlocked.");
    return Task.CompletedTask;
}

static Task TestLimitsSealStateMachine()
{
    var now = new DateTimeOffset(2026, 11, 9, 16, 0, 0, TimeSpan.Zero);
    var seal = MonitorAndControl.ScreenTime.LimitsSealStore.Phase;
    AssertTrue(seal(null, now) == MonitorAndControl.ScreenTime.SealPhase.Off, "Nothing sealed means no protection.");

    var content = new MonitorAndControl.ScreenTime.SealedLimits();
    content.Limits.Add(new() { App = "Game", Minutes = 60, Enabled = true });
    var tooEarly = MonitorAndControl.ScreenTime.LimitsSealStore.TrySeal(null, content, now, out _);
    AssertFalse(tooEarly, "Content cannot be sealed without an approved window.");

    var opened = MonitorAndControl.ScreenTime.LimitsSealStore.Open(null, 1, now);
    AssertTrue(seal(opened, now.AddSeconds(30)) == MonitorAndControl.ScreenTime.SealPhase.Editing, "The window is open for editing.");
    AssertFalse(MonitorAndControl.ScreenTime.LimitsSealStore.TrySeal(opened, content, now.AddSeconds(30), out _), "Sealing waits until the window has closed.");
    AssertTrue(seal(opened, now.AddSeconds(90)) == MonitorAndControl.ScreenTime.SealPhase.SealNow, "After the window DeviceMon is asked to seal.");
    AssertFalse(MonitorAndControl.ScreenTime.LimitsSealStore.TrySeal(opened, content, now.AddMinutes(10), out _), "The grace period for sealing expires.");
    AssertTrue(seal(opened, now.AddMinutes(10)) == MonitorAndControl.ScreenTime.SealPhase.Off, "An abandoned first window leaves nothing to enforce.");

    AssertTrue(MonitorAndControl.ScreenTime.LimitsSealStore.TrySeal(opened, content, now.AddSeconds(90), out var sealedFile), "Content is sealed after the window.");
    AssertTrue(seal(sealedFile, now.AddMinutes(5)) == MonitorAndControl.ScreenTime.SealPhase.Enforce, "A sealed file is enforced.");
    AssertFalse(MonitorAndControl.ScreenTime.LimitsSealStore.TrySeal(sealedFile, content, now.AddMinutes(5), out _), "Sealed content cannot be replaced without a new window.");

    var reopened = MonitorAndControl.ScreenTime.LimitsSealStore.Open(sealedFile, 10, now.AddMinutes(20));
    AssertTrue(reopened.Content != null && seal(reopened, now.AddMinutes(21)) == MonitorAndControl.ScreenTime.SealPhase.Editing, "Re-opening keeps the old copy while editing.");
    AssertTrue(seal(reopened, now.AddMinutes(60)) == MonitorAndControl.ScreenTime.SealPhase.Enforce, "An abandoned edit window falls back to the old sealed copy.");
    var closed = MonitorAndControl.ScreenTime.LimitsSealStore.Close(reopened, now.AddMinutes(22));
    AssertTrue(seal(closed, now.AddMinutes(22)) == MonitorAndControl.ScreenTime.SealPhase.SealNow, "Finishing early asks DeviceMon to seal at once.");

    var a1 = new MonitorAndControl.ScreenTime.SealedLimits();
    a1.Limits.Add(new() { App = "A", Minutes = 1, Enabled = true });
    a1.Limits.Add(new() { App = "B", Minutes = 2, Enabled = true });
    var a2 = new MonitorAndControl.ScreenTime.SealedLimits();
    a2.Limits.Add(new() { App = "B", Minutes = 2, Enabled = true });
    a2.Limits.Add(new() { App = "A", Minutes = 1, Enabled = true });
    AssertTrue(a1.Fingerprint() == a2.Fingerprint(), "The fingerprint ignores order.");
    a2.Limits[0].Minutes = 3;
    AssertTrue(a1.Fingerprint() != a2.Fingerprint(), "The fingerprint notices a changed limit.");
    return Task.CompletedTask;
}

static async Task TestSealedLimitsRestore()
{
    var dir = Path.Combine(Path.GetTempPath(), "DeviceMon-Seal-Test-" + Guid.NewGuid().ToString("N"));
    try
    {
        var db = new MonitorAndControl.Data.UsageDatabase(Path.Combine(dir, "monitor.db"));
        await db.SaveLimitRuleAsync(new MonitorAndControl.Models.AppLimitRule { AppName = "Game", DailyMaxMinutes = 60, Enabled = true });
        await db.SaveScheduleRuleAsync(new MonitorAndControl.Models.ScheduleRule { AppName = "Game", DayOfWeek = "Weekday", StartTime = "15:00", EndTime = "18:00", Enabled = true });
        await db.SaveLimitGroupAsync(new MonitorAndControl.Models.AppLimitGroup { Name = "Games", DailyMaxMinutes = 120, Enabled = true, AppNames = { "Game", "Other" } });
        var sealedContent = await db.ReadLimitsSnapshotAsync();
        var fingerprint = sealedContent.Fingerprint();

        // Tamper with everything: lift the limit, drop the schedule, change and add groups.
        await db.SaveLimitRuleAsync(new MonitorAndControl.Models.AppLimitRule { AppName = "Game", DailyMaxMinutes = 1440, Enabled = false });
        await db.ReplaceScheduleRulesAsync(new List<MonitorAndControl.Models.ScheduleRule>());
        var groups = await db.GetLimitGroupsAsync();
        groups[0].DailyMaxMinutes = 1440;
        groups[0].AppNames.Clear();
        await db.SaveLimitGroupAsync(groups[0]);
        await db.SaveLimitGroupAsync(new MonitorAndControl.Models.AppLimitGroup { Name = "Extra", DailyMaxMinutes = 5, Enabled = true });
        AssertTrue((await db.ReadLimitsSnapshotAsync()).Fingerprint() != fingerprint, "The tampering is visible in the fingerprint.");

        await db.ApplyLimitsSnapshotAsync(sealedContent);
        AssertTrue((await db.ReadLimitsSnapshotAsync()).Fingerprint() == fingerprint, "Applying the sealed copy restores limits, schedule and groups.");
        var restored = await db.GetLimitGroupsAsync();
        AssertTrue(restored.Count == 1 && restored[0].Name == "Games" && restored[0].AppNames.Count == 2, "An extra group is removed and members come back.");
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }
}

static Task TestSiteNames()
{
    string? Host(string? raw) => MonitorAndControl.Services.SiteName.TryGetHost(raw, out var h) ? h : null;
    AssertTrue(Host("https://www.YouTube.com/watch?v=abc&t=1") == "youtube.com", "The path, query and www are dropped.");
    AssertTrue(Host("youtube.com/watch?v=abc") == "youtube.com", "A value without a scheme works (Chrome hides it).");
    AssertTrue(Host("https://user:pw@example.org:8443/a#b") == "example.org", "User info and port are dropped.");
    AssertTrue(Host("https://mail.google.com/") == "mail.google.com", "Subdomains are kept.");
    AssertTrue(Host("http://localhost:5000/index.html") == "localhost", "Localhost counts as a site.");
    AssertTrue(Host("  Example.COM  ") == "example.com", "Case and spaces are normalised.");
    AssertTrue(Host("chrome://newtab") == null && Host("edge://settings") == null && Host("about:blank") == null && Host("file:///C:/x.txt") == null,
        "Internal browser pages are not websites.");
    AssertTrue(Host("how to bake bread") == null && Host("bake") == null, "Search text being typed is never recorded.");
    AssertTrue(Host("") == null && Host(null) == null && Host(new string('a', 3000)) == null, "Empty and absurd values are ignored.");
    AssertTrue(MonitorAndControl.Services.SiteName.IsPrivateTitle("New tab - Google Chrome - Incognito"), "Incognito is recognised.");
    AssertTrue(MonitorAndControl.Services.SiteName.IsPrivateTitle("Bing and 1 more page - Personal - Microsoft Edge [InPrivate]"), "InPrivate is recognised.");
    AssertFalse(MonitorAndControl.Services.SiteName.IsPrivateTitle("YouTube - Google Chrome"), "A normal window is not private.");
    return Task.CompletedTask;
}

static async Task TestSiteUsageStorage()
{
    var dir = Path.Combine(Path.GetTempPath(), "DeviceMon-Sites-Test-" + Guid.NewGuid().ToString("N"));
    try
    {
        var db = new MonitorAndControl.Data.UsageDatabase(Path.Combine(dir, "monitor.db"));
        var day = new DateTime(2026, 11, 9);
        await db.AddSiteUsageAsync(day, new Dictionary<string, long> { ["youtube.com"] = 60, ["school.example"] = 30 });
        await db.AddSiteUsageAsync(day, new Dictionary<string, long> { ["youtube.com"] = 40, ["zero.example"] = 0 });
        await db.AddSiteUsageAsync(day.AddDays(1), new Dictionary<string, long> { ["youtube.com"] = 10 });
        var rows = await db.GetSiteUsageRangeAsync(day, day);
        AssertTrue(rows.Count == 2 && rows.Single(r => r.Host == "youtube.com").Seconds == 100, "Time adds up per host and day; zero seconds are skipped.");
        AssertTrue((await db.GetSiteUsageRangeAsync(day, day.AddDays(1))).Sum(r => r.Seconds) == 140, "A range includes every day in it.");
        await db.ClearUsageHistoryAsync();
        AssertTrue((await db.GetSiteUsageRangeAsync(day, day.AddDays(1))).Count == 0, "Clearing history also clears website time.");
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }
}

static Task TestSiteLimitLogic()
{
    var rules = new List<MonitorAndControl.Models.SiteLimit>
    {
        new() { Host = "youtube.com", DailyMaxMinutes = 30, Enabled = true },
        new() { Host = "tiktok.com", DailyMaxMinutes = 0, Enabled = true },
        new() { Host = "reddit.com", DailyMaxMinutes = 0, Enabled = false },
        new() { Host = "chess.com", DailyMaxMinutes = 60, Enabled = true }
    };
    var usage = new List<MonitorAndControl.Models.SiteUsageRecord>
    {
        new() { Host = "youtube.com", Seconds = 900 }, new() { Host = "m.youtube.com", Seconds = 900 },
        new() { Host = "notyoutube.com", Seconds = 99999 }, new() { Host = "chess.com", Seconds = 600 }
    };
    var status = MonitorAndControl.Services.SiteLimitLogic.Evaluate(rules, usage);
    AssertTrue(status.Single(s => s.Host == "youtube.com").UsedSeconds == 1800, "A site and its subdomains share one allowance, and look-alike names do not count.");
    AssertTrue(status.Single(s => s.Host == "youtube.com").Blocked, "Reaching the daily allowance blocks the site.");
    AssertTrue(status.Single(s => s.Host == "tiktok.com").Blocked, "Zero minutes blocks the site outright.");
    AssertFalse(status.Single(s => s.Host == "reddit.com").Blocked, "A switched-off rule blocks nothing.");
    AssertFalse(status.Single(s => s.Host == "chess.com").Blocked, "Time left on the allowance keeps the site open.");
    var blocked = MonitorAndControl.Services.SiteLimitLogic.BlockedHosts(status);
    AssertTrue(blocked.Count == 2 && blocked.Contains("youtube.com") && blocked.Contains("tiktok.com"), "Only blocked sites are reported to the service.");

    string? Norm(string? raw) => MonitorAndControl.ScreenTime.SiteBlocking.TryNormalizeHost(raw, out var h) ? h : null;
    AssertTrue(Norm(" WWW.YouTube.com ") == "youtube.com", "Site names are tidied.");
    AssertTrue(Norm("youtube") == null && Norm("a b.com") == null && Norm("youtube.com/watch") == null && Norm("") == null && Norm("-bad.com") == null,
        "Bare words, spaces, paths and bad names are refused.");
    AssertTrue(MonitorAndControl.ScreenTime.SiteBlocking.Covers("youtube.com", "music.youtube.com") && !MonitorAndControl.ScreenTime.SiteBlocking.Covers("youtube.com", "xyoutube.com"),
        "Subdomains are covered, look-alikes are not.");
    return Task.CompletedTask;
}

static Task TestBlocklistMerge()
{
    var current = new[] { "corporate-blocked.example", "old-ours.com" };
    var merged = MonitorAndControl.ScreenTime.SiteBlocking.Merge(current, new[] { "old-ours.com" }, new[] { "youtube.com", "tiktok.com" });
    AssertTrue(merged.SequenceEqual(new[] { "corporate-blocked.example", "youtube.com", "tiktok.com" }),
        "Entries from other sources stay; ours are replaced by the current set.");
    AssertTrue(MonitorAndControl.ScreenTime.SiteBlocking.Merge(new[] { "old-ours.com" }, new[] { "old-ours.com" }, Array.Empty<string>()).Count == 0,
        "With nothing to block, our old entries disappear.");
    AssertTrue(MonitorAndControl.ScreenTime.SiteBlocking.Merge(new[] { "YouTube.com" }, Array.Empty<string>(), new[] { "youtube.com" }).Count == 1,
        "An entry that already exists is not added twice.");
    var firefox = MonitorAndControl.ScreenTime.SiteBlocking.FirefoxEntries(new[] { "youtube.com" });
    AssertTrue(firefox.SequenceEqual(new[] { "*://youtube.com/*", "*://*.youtube.com/*" }), "Firefox gets match patterns for the site and its subdomains.");
    return Task.CompletedTask;
}

static async Task TestSealedSites()
{
    var dir = Path.Combine(Path.GetTempPath(), "DeviceMon-SealSites-Test-" + Guid.NewGuid().ToString("N"));
    try
    {
        var db = new MonitorAndControl.Data.UsageDatabase(Path.Combine(dir, "monitor.db"));
        await db.SaveSiteLimitAsync(new MonitorAndControl.Models.SiteLimit { Host = "tiktok.com", DailyMaxMinutes = 0, Enabled = true });
        var sealedContent = await db.ReadLimitsSnapshotAsync();
        AssertTrue(sealedContent.Sites.Count == 1, "Website rules are part of the sealed snapshot.");
        var fingerprint = sealedContent.Fingerprint();

        await db.SaveSiteLimitAsync(new MonitorAndControl.Models.SiteLimit { Host = "tiktok.com", DailyMaxMinutes = 1440, Enabled = false });
        await db.SaveSiteLimitAsync(new MonitorAndControl.Models.SiteLimit { Host = "extra.com", DailyMaxMinutes = 5, Enabled = true });
        AssertTrue((await db.ReadLimitsSnapshotAsync()).Fingerprint() != fingerprint, "Changing a website rule changes the fingerprint.");

        await db.ApplyLimitsSnapshotAsync(sealedContent);
        var rules = await db.GetSiteLimitsAsync();
        AssertTrue(rules.Count == 1 && rules[0].Host == "tiktok.com" && rules[0].DailyMaxMinutes == 0 && rules[0].Enabled, "The sealed website rules are restored.");
        AssertTrue((await db.ReadLimitsSnapshotAsync()).Fingerprint() == fingerprint, "The database matches the seal again.");
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }
}

static Task TestReviewFixes()
{
    var p = new MonitorAndControl.ScreenTime.ScreenTimePolicy
    {
        UserSid = "S-1-5-21-1-2-3-1001", DisplayName = "kid", BedtimeStart = "22:00", BedtimeEnd = "07:00", WeekendBedtimeStart = "23:30"
    };
    p.Validate();
    p.WeekendBedtimeStart = "00:30";
    var refused = false;
    try { p.Validate(); } catch (InvalidDataException) { refused = true; }
    AssertTrue(refused, "A weekend bedtime after midnight would read as a daytime window and is refused.");
    p.BedtimeStart = "13:00"; p.BedtimeEnd = "15:00"; p.WeekendBedtimeStart = "14:00";
    p.Validate();

    var marker = "2026-11-09T21:00:00.0000000+01:00|Monitor process was missing and watchdog relaunched it.";
    var when = MonitorAndControl.Services.RepeatedCloseDetector.EventTime(marker, DateTimeOffset.UtcNow);
    AssertTrue(when == new DateTimeOffset(2026, 11, 9, 20, 0, 0, TimeSpan.Zero), "The restart time comes from the marker.");
    var history = new List<DateTimeOffset>();
    AssertTrue(MonitorAndControl.Services.RepeatedCloseDetector.AddEvent(history, when), "The first report of a restart is recorded.");
    AssertFalse(MonitorAndControl.Services.RepeatedCloseDetector.AddEvent(history, when), "A retried report of the same restart is not recorded again.");
    AssertTrue(history.Count == 1, "One restart is one entry.");

    var json = "{\"Limits\":null,\"Schedule\":[null],\"Groups\":[{\"Name\":null,\"Members\":null}],\"Sites\":null}";
    var content = System.Text.Json.JsonSerializer.Deserialize<MonitorAndControl.ScreenTime.SealedLimits>(json)!.Normalized();
    AssertTrue(content.IsReasonable() && content.Fingerprint().Length == 64, "Content with missing lists is repaired instead of crashing the service.");
    return Task.CompletedTask;
}

static Task TestPolicyPlan()
{
    var none = MonitorAndControl.ScreenTime.SiteBlocking.ComputePlan(new[] { "tiktok.com" }, new[] { "school.example" }, false, false, null);
    AssertTrue(none.BlockHosts.Single() == "tiktok.com" && none.AllowHosts.Count == 0 && !none.AllowOnly && !none.SafeBrowsing,
        "Without allow-list mode no site is added to the allow list.");

    var sealedContent = new MonitorAndControl.ScreenTime.SealedLimits { AllowOnly = true, SafeBrowsing = true };
    sealedContent.Sites.Add(new() { Host = "vip.example", Minutes = 0, Enabled = true });
    sealedContent.Sites.Add(new() { Host = "school.example", Minutes = -1, Enabled = true });
    sealedContent.Sites.Add(new() { Host = "games.example", Minutes = 30, Enabled = true });
    sealedContent.Sites.Add(new() { Host = "off.example", Minutes = -1, Enabled = false });
    var plan = MonitorAndControl.ScreenTime.SiteBlocking.ComputePlan(Array.Empty<string>(), Array.Empty<string>(), false, false, sealedContent);
    AssertTrue(plan.AllowOnly && plan.SafeBrowsing, "The sealed switches hold even if DeviceMon reports them off.");
    AssertTrue(plan.BlockHosts.SequenceEqual(new[] { "vip.example" }), "A sealed block stays whatever DeviceMon reports.");
    AssertTrue(plan.AllowHosts.OrderBy(x => x).SequenceEqual(new[] { "games.example", "school.example" }), "Sealed allowed sites are used; switched-off rules are not.");

    var used = MonitorAndControl.ScreenTime.SiteBlocking.ComputePlan(new[] { "games.example" }, new[] { "school.example" }, true, false, sealedContent);
    AssertFalse(used.AllowHosts.Contains("games.example"), "A site whose daily allowance is used up is not allowed, so its block wins.");
    AssertTrue(used.BlockHosts.Contains("games.example") && used.BlockHosts.Contains("vip.example"), "Used-up and sealed blocks are both blocked.");

    var withSeal = new MonitorAndControl.ScreenTime.SealedLimits();
    var withoutSeal = new MonitorAndControl.ScreenTime.SealedLimits { AllowOnly = true };
    AssertTrue(withSeal.Fingerprint() != withoutSeal.Fingerprint(), "The seal notices the allow-list switch.");

    var rules = new List<MonitorAndControl.Models.SiteLimit>
    {
        new() { Host = "school.example", DailyMaxMinutes = -1, Enabled = true },
        new() { Host = "games.example", DailyMaxMinutes = 1, Enabled = true }
    };
    var usage = new List<MonitorAndControl.Models.SiteUsageRecord> { new() { Host = "games.example", Seconds = 120 } };
    var status = MonitorAndControl.Services.SiteLimitLogic.Evaluate(rules, usage);
    AssertFalse(status.Single(x => x.Host == "school.example").Blocked, "An always-allowed site is never blocked.");
    AssertTrue(MonitorAndControl.Services.SiteLimitLogic.AllowedHosts(status).SequenceEqual(new[] { "school.example" }), "Only sites with time left are allowed.");
    return Task.CompletedTask;
}

static async Task TestActivityTimeline()
{
    var dir = Path.Combine(Path.GetTempPath(), "DeviceMon-Timeline-Test-" + Guid.NewGuid().ToString("N"));
    try
    {
        var db = new MonitorAndControl.Data.UsageDatabase(Path.Combine(dir, "monitor.db"));
        await db.RecordUsageBatchAsync(new[]
        {
            ("Game", "game.exe", 120, 0), ("Music", "music.exe", 0, 300), ("Chrome", "chrome.exe", 60, 10)
        }, Array.Empty<(int GroupId, int Seconds)>());
        await db.RecordUsageBatchAsync(new[] { ("Game", "game.exe", 30, 0) }, Array.Empty<(int GroupId, int Seconds)>());
        await db.AddSiteUsageAsync(DateTime.Today, new Dictionary<string, long> { ["youtube.com"] = 45 });

        var rows = await db.GetActivityDayAsync(DateTime.Today);
        var hour = DateTime.Now.Hour;
        AssertTrue(rows.All(r => r.Hour == hour), "Everything recorded just now falls in the current hour.");
        AssertTrue(rows.Single(r => r.Kind == "app" && r.Name == "Game").Seconds == 150, "Foreground time adds up per app and hour.");
        AssertFalse(rows.Any(r => r.Name == "Music"), "Background-only time is not part of what was on screen.");
        AssertTrue(rows.Single(r => r.Kind == "site" && r.Name == "youtube.com").Seconds == 45, "Website time is stored by hour too.");
        AssertTrue((await db.GetActivityDayAsync(DateTime.Today.AddDays(-1))).Count == 0, "Other days are empty.");
        await db.ClearUsageHistoryAsync();
        AssertTrue((await db.GetActivityDayAsync(DateTime.Today)).Count == 0, "Clearing history clears the timeline.");
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }
}

static Task TestPushValidation()
{
    string? V(string provider, string server = "https://ntfy.sh", string topic = "", string token = "", string chat = "") =>
        MonitorAndControl.Services.PushSender.Validate(provider, server, topic, token, chat);
    AssertTrue(V("") == null, "Turning notifications off needs nothing else.");
    AssertTrue(V("ntfy", topic: "family-pc-7f3k9x2q") == null, "A long topic on the default server is fine.");
    AssertTrue(V("ntfy", topic: "abc") != null, "A short, guessable topic is refused.");
    AssertTrue(V("ntfy", topic: "has space here") != null && V("ntfy", topic: "bad/topic/name") != null, "A topic with spaces or slashes is refused.");
    AssertTrue(V("ntfy", server: "http://ntfy.sh", topic: "family-pc-7f3k9x2q") != null, "An unencrypted server address is refused.");
    AssertTrue(V("ntfy", server: "not a url", topic: "family-pc-7f3k9x2q") != null, "A server that is not an address is refused.");
    AssertTrue(V("telegram", token: "123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw", chat: "-1001234567890") == null, "A well-formed bot token and chat ID are accepted.");
    AssertTrue(V("telegram", token: "hello", chat: "123") != null && V("telegram", token: "123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw", chat: "abc") != null,
        "A bad token or chat ID is refused.");
    AssertTrue(V("sms") != null, "An unknown service is refused.");
    return Task.CompletedTask;
}

static async Task TestHomeworkMode()
{
    var now = new DateTimeOffset(2026, 11, 9, 16, 0, 0, TimeSpan.Zero);
    AssertFalse(MonitorAndControl.ScreenTime.HomeworkState.IsActive(null, now), "No file means no homework mode.");
    var shortRun = MonitorAndControl.ScreenTime.HomeworkState.Start(1, now);
    AssertTrue(shortRun.UntilUtc == now.AddMinutes(10), "A very short time is raised to the minimum.");
    AssertTrue(MonitorAndControl.ScreenTime.HomeworkState.Start(9999, now).UntilUtc == now.AddMinutes(240), "A very long time is capped at four hours.");
    var run = MonitorAndControl.ScreenTime.HomeworkState.Start(60, now);
    AssertTrue(MonitorAndControl.ScreenTime.HomeworkState.IsActive(run, now.AddMinutes(59)), "It is on until the end time.");
    AssertFalse(MonitorAndControl.ScreenTime.HomeworkState.IsActive(run, now.AddMinutes(60)), "It ends by itself.");

    var plain = MonitorAndControl.ScreenTime.SiteBlocking.ComputePlan(Array.Empty<string>(), new[] { "school.example" }, false, false, null);
    AssertFalse(plain.AllowOnly, "Without homework mode nothing changes.");
    var homework = MonitorAndControl.ScreenTime.SiteBlocking.ComputePlan(Array.Empty<string>(), new[] { "school.example" }, false, false, null, homework: true);
    AssertTrue(homework.AllowOnly && homework.AllowHosts.Single() == "school.example", "Homework mode allows only the listed sites.");

    var dir = Path.Combine(Path.GetTempPath(), "DeviceMon-Homework-Test-" + Guid.NewGuid().ToString("N"));
    try
    {
        var db = new MonitorAndControl.Data.UsageDatabase(Path.Combine(dir, "monitor.db"));
        await db.SetHomeworkAppsAsync(new[] { "Minecraft", "minecraft", "Roblox" });
        var apps = await db.GetHomeworkAppsAsync();
        AssertTrue(apps.Count == 2, "The same app is listed once, whatever its capitals.");
        var sealedContent = await db.ReadLimitsSnapshotAsync();
        var fingerprint = sealedContent.Fingerprint();
        AssertTrue(sealedContent.HomeworkApps.Count == 2, "The homework apps are part of the sealed snapshot.");
        await db.SetHomeworkAppsAsync(Array.Empty<string>());
        AssertTrue((await db.ReadLimitsSnapshotAsync()).Fingerprint() != fingerprint, "Emptying the list changes the fingerprint.");
        await db.ApplyLimitsSnapshotAsync(sealedContent);
        AssertTrue((await db.GetHomeworkAppsAsync()).Count == 2, "The sealed list is restored.");
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }
}

static Task TestNewApps()
{
    var installed = new List<(string Key, string Name)> { ("steam.exe", "Steam"), ("discord.exe", "Discord"), ("DISCORD.EXE", "Discord"), ("", "No key") };
    var added = MonitorAndControl.Services.NewAppWatcher.FindNew(installed, new[] { "Steam.exe" });
    AssertTrue(added.Count == 1 && added[0].Name == "Discord", "Only programs not seen before are new, once each, ignoring case.");
    AssertTrue(MonitorAndControl.Services.NewAppWatcher.FindNew(installed, new[] { "steam.exe", "discord.exe" }).Count == 0, "Nothing new means no alert.");
    var many = Enumerable.Range(1, 12).Select(i => ($"app{i}.exe", $"App {i}")).ToList();
    var text = MonitorAndControl.Services.NewAppWatcher.Describe(many);
    AssertTrue(text.Contains("App 1,") && text.Contains("and 4 more") && !text.Contains("App 9"), "A long list is shortened.");
    AssertTrue(MonitorAndControl.Services.NewAppWatcher.Describe(new[] { ("x.exe", "") }).Contains("x.exe"), "An app without a display name falls back to its file name.");
    return Task.CompletedTask;
}

static Task TestPushLinks()
{
    AssertTrue(MonitorAndControl.Services.PushSender.BuildLink("http://pc:5000", "screen-time") == "http://pc:5000/index.html#screen-time", "The link points at the dashboard page.");
    AssertTrue(MonitorAndControl.Services.PushSender.BuildLink("https://home.example.com/monitor/", "limits") == "https://home.example.com/monitor/index.html#limits", "A path prefix behind a reverse proxy is kept.");
    AssertTrue(MonitorAndControl.Services.PushSender.BuildLink("http://pc:5000/index.html", "live") == "http://pc:5000/index.html#live", "A pasted index.html is not doubled.");
    AssertTrue(MonitorAndControl.Services.PushSender.BuildLink("http://pc:5000", "../evil") == "http://pc:5000/index.html#live", "An odd page name falls back to the live view.");
    AssertTrue(MonitorAndControl.Services.PushSender.BuildLink("ftp://pc", "live") == null && MonitorAndControl.Services.PushSender.BuildLink("", "live") == null, "Only http and https addresses are linked.");
    AssertTrue(MonitorAndControl.Services.PushSender.PageFor("pc_time_request") == "screen-time" && MonitorAndControl.Services.PushSender.PageFor("new_app") == "limits"
        && MonitorAndControl.Services.PushSender.PageFor("watchdog_restart") == "logs" && MonitorAndControl.Services.PushSender.PageFor("anything") == "live", "Each kind of alert opens a sensible page.");
    return Task.CompletedTask;
}

static async Task TestPerAccountLimits()
{
    var dir = Path.Combine(Path.GetTempPath(), "DeviceMon-Account-Test-" + Guid.NewGuid().ToString("N"));
    const string anna = "S-1-5-21-1-2-3-1001";
    const string ben = "S-1-5-21-1-2-3-1002";
    try
    {
        var db = new MonitorAndControl.Data.UsageDatabase(Path.Combine(dir, "monitor.db"));
        await db.SaveUserLimitRuleAsync(new MonitorAndControl.Models.AppLimitRule { AppName = "Minecraft", UserSid = anna, DailyMaxMinutes = 30, Enabled = true });
        await db.SaveUserLimitRuleAsync(new MonitorAndControl.Models.AppLimitRule { AppName = "Minecraft", UserSid = ben, DailyMaxMinutes = 90, Enabled = true });
        await db.SaveUserLimitRuleAsync(new MonitorAndControl.Models.AppLimitRule { AppName = "Minecraft", UserSid = anna, DailyMaxMinutes = 45, Enabled = true });
        var rules = await db.GetUserLimitRulesAsync();
        AssertTrue(rules.Count == 2 && rules.Single(r => r.UserSid == anna).DailyMaxMinutes == 45, "Each account has its own rule per app, and saving again updates it.");
        AssertTrue((await db.GetLimitRulesAsync()).Count == 0, "Account limits do not appear among the shared limits.");

        var none = Array.Empty<(int GroupId, int Seconds)>();
        await db.RecordUsageBatchAsync(new[] { ("Minecraft", "minecraft.exe", 600, 0) }, none, anna);
        await db.RecordUsageBatchAsync(new[] { ("Minecraft", "minecraft.exe", 120, 0) }, none, ben);
        await db.RecordUsageBatchAsync(new[] { ("Minecraft", "minecraft.exe", 60, 0) }, none, anna);
        var annaUsage = await db.GetUserAppUsageTodayAsync(anna);
        var benUsage = await db.GetUserAppUsageTodayAsync(ben);
        AssertTrue(annaUsage["Minecraft"] == 660 && benUsage["Minecraft"] == 120, "Each account's time is counted on its own.");
        AssertTrue((await db.GetTodayUsageAsync()).Single(u => u.AppName == "Minecraft").TotalSeconds == 780, "The shared total still adds everyone up.");
        await db.RecordUsageBatchAsync(new[] { ("Minecraft", "minecraft.exe", 10, 0) }, none);
        AssertTrue((await db.GetUserAppUsageTodayAsync(anna))["Minecraft"] == 660, "Usage recorded without an account is not credited to anyone.");

        var snapshot = await db.ReadLimitsSnapshotAsync();
        var fingerprint = snapshot.Fingerprint();
        AssertTrue(snapshot.UserLimits.Count == 2, "Account limits are part of the sealed snapshot.");
        await db.DeleteUserLimitRuleAsync("Minecraft", anna);
        await db.SaveUserLimitRuleAsync(new MonitorAndControl.Models.AppLimitRule { AppName = "Roblox", UserSid = ben, DailyMaxMinutes = 5, Enabled = true });
        AssertTrue((await db.ReadLimitsSnapshotAsync()).Fingerprint() != fingerprint, "Changing an account limit changes the fingerprint.");
        await db.ApplyLimitsSnapshotAsync(snapshot);
        var restored = await db.GetUserLimitRulesAsync();
        AssertTrue(restored.Count == 2 && restored.Any(r => r.UserSid == anna && r.DailyMaxMinutes == 45) && restored.All(r => r.AppName == "Minecraft"), "The sealed account limits are restored.");

        await db.ClearUsageHistoryAsync();
        AssertTrue((await db.GetUserAppUsageTodayAsync(anna)).Count == 0, "Clearing history clears each account's usage too.");
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }
    AssertFalse(MonitorAndControl.Services.AccountIdentity.IsValidSid(anna + "\n"), "A trailing new line is not part of a SID.");
    AssertTrue(MonitorAndControl.Services.AccountIdentity.IsValidSid(anna) && MonitorAndControl.Services.AccountIdentity.IsValidSid("S-1-12-1-111-222-333-444") && !MonitorAndControl.Services.AccountIdentity.IsValidSid("S-1-5-18")
        && !MonitorAndControl.Services.AccountIdentity.IsValidSid("") && !MonitorAndControl.Services.AccountIdentity.IsValidSid("S-1-5-21-1-2-3-1001'; DROP"),
        "Only real local account SIDs are accepted.");
}

static Task TestTimeLeftView()
{
    var now = new DateTimeOffset(2026, 11, 9, 16, 0, 0, TimeSpan.Zero);
    var apps = new[] { ("Minecraft", 2100L, 3600L), ("Roblox", 600L, 600L) };
    var view = MonitorAndControl.Services.TimeLeftReport.Build(7800, 45, false, null, now.AddMinutes(30), apps, now);
    AssertTrue(view.Lines.Contains("PC time left today: 2h 10m."), "The PC time left is shown in hours and minutes.");
    AssertTrue(view.Lines.Contains("Bedtime in 45 minutes."), "Bedtime is announced.");
    AssertTrue(view.Lines.Any(l => l.StartsWith("Homework mode until")), "Homework mode shows its end time.");
    AssertTrue(view.Lines.Contains("Minecraft: 25m left of 1h 0m."), "An app shows what is left of its allowance.");
    AssertTrue(view.Lines.Contains("Roblox: no time left today (used 10m)."), "A used-up app says so.");
    AssertTrue(view.Lines.IndexOf("Roblox: no time left today (used 10m).") < view.Lines.IndexOf("Minecraft: 25m left of 1h 0m."), "The app with the least time left comes first.");
    AssertTrue(view.Tooltip.Length <= 120 && view.Tooltip.StartsWith("PC time left 2h 10m"), "The icon tooltip is short and starts with the PC time.");

    var empty = MonitorAndControl.Services.TimeLeftReport.Build(null, null, false, null, null, Array.Empty<(string, long, long)>(), now);
    AssertTrue(empty.Lines.Single() == "No time limits are set for this account right now." && empty.Tooltip == "No time limits right now", "With nothing set it says so plainly.");
    var locked = MonitorAndControl.Services.TimeLeftReport.Build(600, null, true, now.AddMinutes(20), null, Array.Empty<(string, long, long)>(), now);
    AssertTrue(locked.Lines[0].StartsWith("This PC is locked until") && locked.Lines.Contains("It is bedtime."), "A lock and bedtime are shown.");
    AssertTrue(MonitorAndControl.Services.TimeLeftReport.Duration(30) == "less than a minute" && MonitorAndControl.Services.TimeLeftReport.Duration(3600) == "1h 0m", "Durations read naturally.");
    return Task.CompletedTask;
}

static async Task TestLeaseClaim()
{
    var dir = Path.Combine(Path.GetTempPath(), "DeviceMon-Lease-Test-" + Guid.NewGuid().ToString("N"));
    try
    {
        var first = new MonitorAndControl.Data.UsageDatabase(Path.Combine(dir, "monitor.db"));
        var second = new MonitorAndControl.Data.UsageDatabase(Path.Combine(dir, "monitor.db"));
        AssertTrue(await first.TryClaimLeaseAsync("scan", TimeSpan.FromMinutes(20)), "The first claim wins.");
        AssertFalse(await second.TryClaimLeaseAsync("scan", TimeSpan.FromMinutes(20)), "A second account cannot take it while it is held.");
        AssertTrue(await second.TryClaimLeaseAsync("other-job", TimeSpan.FromMinutes(20)), "A different job is independent.");
        AssertTrue(await first.TryClaimLeaseAsync("short", TimeSpan.FromMilliseconds(1)), "A short claim is granted.");
        await Task.Delay(50);
        AssertTrue(await second.TryClaimLeaseAsync("short", TimeSpan.FromMinutes(1)), "An expired claim can be taken by someone else.");
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }
}

static async Task TestAccountUsageCase()
{
    var dir = Path.Combine(Path.GetTempPath(), "DeviceMon-Case-Test-" + Guid.NewGuid().ToString("N"));
    const string anna = "S-1-5-21-1-2-3-1001";
    try
    {
        var db = new MonitorAndControl.Data.UsageDatabase(Path.Combine(dir, "monitor.db"));
        var none = Array.Empty<(int GroupId, int Seconds)>();
        await db.RecordUsageBatchAsync(new[] { ("Minecraft", "minecraft.exe", 100, 0) }, none, anna);
        await db.RecordUsageBatchAsync(new[] { ("minecraft", "minecraft.exe", 50, 0) }, none, anna);
        var usage = await db.GetUserAppUsageTodayAsync(anna);
        AssertTrue(usage.Count == 1 && usage["MINECRAFT"] == 150, "The same app in different capitals is one entry whose time adds up.");
        await db.SaveUserLimitRuleAsync(new MonitorAndControl.Models.AppLimitRule { AppName = "Minecraft", UserSid = anna, DailyMaxMinutes = 30, Enabled = true });
        await db.SaveUserLimitRuleAsync(new MonitorAndControl.Models.AppLimitRule { AppName = "minecraft", UserSid = anna, DailyMaxMinutes = 45, Enabled = true });
        AssertTrue((await db.GetUserLimitRulesAsync(anna)).Single().DailyMaxMinutes == 45, "A rule is one per app and account whatever the capitals.");
        AssertTrue((await db.GetUserLimitRulesAsync("S-1-5-21-9-9-9-9")).Count == 0, "Asking for one account returns only its rules.");
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }
}

static Task TestPinnedTimeZone()
{
    var now = new DateTimeOffset(2026, 11, 2, 18, 0, 0, TimeSpan.Zero);
    var first = MonitorAndControl.ScreenTime.ParentClock.Observe(null, "W. Europe Standard Time", now);
    AssertTrue(first.PinnedId == "W. Europe Standard Time" && first.ChangedUtc == null, "The first zone seen is pinned.");
    var moved = MonitorAndControl.ScreenTime.ParentClock.Observe(first, "Tokyo Standard Time", now);
    AssertTrue(moved.PinnedId == "W. Europe Standard Time" && moved.SystemId == "Tokyo Standard Time" && moved.ChangedUtc == now, "A change is recorded but the rules keep the pinned zone.");
    var later = MonitorAndControl.ScreenTime.ParentClock.Observe(moved, "Tokyo Standard Time", now.AddMinutes(5));
    AssertTrue(later.ChangedUtc == now, "The same change keeps its first time, so it is announced once.");
    var other = MonitorAndControl.ScreenTime.ParentClock.Observe(later, "Pacific Standard Time", now.AddMinutes(9));
    AssertTrue(other.ChangedUtc == now.AddMinutes(9), "A different zone is a new change.");
    var back = MonitorAndControl.ScreenTime.ParentClock.Observe(other, "W. Europe Standard Time", now.AddMinutes(10));
    AssertTrue(back.ChangedUtc == null && back.PinnedId == "W. Europe Standard Time", "Switching back clears the change.");
    var accepted = MonitorAndControl.ScreenTime.ParentClock.Accept("Tokyo Standard Time");
    AssertTrue(accepted.PinnedId == "Tokyo Standard Time" && accepted.ChangedUtc == null, "Accepting pins the new zone.");
    var zone = MonitorAndControl.ScreenTime.ParentClock.Resolve(new MonitorAndControl.ScreenTime.TimeZoneState { PinnedId = "Tokyo Standard Time" });
    AssertTrue(zone.Id == "Tokyo Standard Time", "The pinned zone is the one the rules use.");
    AssertTrue(MonitorAndControl.ScreenTime.ParentClock.Resolve(new MonitorAndControl.ScreenTime.TimeZoneState { PinnedId = "No/Such zone" }).Id == TimeZoneInfo.Local.Id
        && MonitorAndControl.ScreenTime.ParentClock.Resolve(null).Id == TimeZoneInfo.Local.Id, "An unknown or missing zone falls back to Windows' own.");
    return Task.CompletedTask;
}

static Task TestHeartbeatUrls()
{
    foreach (var good in new[] { "", "https://hc-ping.com/5f1c2d6e-1111-2222-3333-444455556666", "https://example.org/ping?x=1" })
        AssertTrue(MonitorAndControl.ScreenTime.Heartbeat.IsValidUrl(good), "Accepted: " + good);
    foreach (var bad in new[] { "http://hc-ping.com/x", "https://localhost/ping", "https://192.168.1.5/ping", "https://10.0.0.1/x",
                                "https://127.0.0.1/x", "https://[::1]/x", "https://pc/x", "https://user:pw@hc-ping.com/x", "ftp://hc-ping.com/x",
                                "https://[::ffff:192.168.1.1]/x", "https://[::ffff:127.0.0.1]/x", "https://0.0.0.0/x", "https://[fd00::1]/x", "https://100.64.0.1/x",
                                "https://hc-ping.com/" + new string('a', 300) })
        AssertFalse(MonitorAndControl.ScreenTime.Heartbeat.IsValidUrl(bad), "Refused: " + bad);
    AssertTrue(MonitorAndControl.ScreenTime.Heartbeat.IsValidUrl("https://8.8.8.8/x")
        && MonitorAndControl.ScreenTime.Heartbeat.IsPublicAddress(System.Net.IPAddress.Parse("2606:4700::1111"))
        && !MonitorAndControl.ScreenTime.Heartbeat.IsPublicAddress(System.Net.IPAddress.Parse("::ffff:10.0.0.5")), "Public addresses pass, home-network ones never do.");
    return Task.CompletedTask;
}

static Task TestAlertButtons()
{
    AssertTrue(PushSender.BuildLink("http://pc:5000", "screen-time&grant=15&request=0123abcd") == "http://pc:5000/index.html#screen-time&grant=15&request=0123abcd",
        "An Add button opens PC Time with the request filled in.");
    AssertTrue(PushSender.BuildLink("http://pc:5000", "screen-time&grant=15&request=<x>") == "http://pc:5000/index.html#live", "Odd parameters fall back to the live view.");
    AssertTrue(PushSender.BuildBase("https://home.example.com/monitor/index.html") == "https://home.example.com/monitor", "The base keeps a reverse-proxy prefix.");

    var token = new string('a', 43);
    var buttons = new[] { new PushButton("Add 15 min", "screen-time&grant=15&request=r1"), new PushButton("Not now", DeclineToken: token), new PushButton("x"), new PushButton("y") };
    var actions = System.Text.Json.JsonSerializer.Serialize(PushSender.NtfyActions("http://pc:5000", buttons));
    AssertTrue(actions.Contains("\"action\":\"view\"") && actions.Contains("index.html#screen-time\\u0026grant=15"), "ntfy opens the dashboard for Add.");
    AssertTrue(actions.Contains("\"action\":\"http\"") && actions.Contains("http://pc:5000/api/quick/decline") && actions.Contains(token), "ntfy sends Not now straight from the phone.");
    AssertTrue(System.Text.Json.JsonDocument.Parse(actions).RootElement.GetArrayLength() == 3, "ntfy takes at most three buttons.");
    var keyboard = System.Text.Json.JsonSerializer.Serialize(PushSender.TelegramKeyboard("http://pc:5000", buttons.Take(2)));
    AssertTrue(keyboard.Contains("http://pc:5000/quick.html#" + token) && keyboard.Contains("inline_keyboard"), "Telegram opens a page that asks before declining.");
    AssertTrue(PushSender.PageFor("timezone_changed") == "screen-time", "A time zone alert opens PC Time.");
    return Task.CompletedTask;
}

static async Task TestQuickActions()
{
    using var db = CreateTempDatabase();
    var now = DateTimeOffset.UtcNow;
    var token = await QuickActions.CreateDeclineAsync(db, "S-1-5-21-1-2-3-1001", "req1", now.AddHours(1));
    AssertTrue(QuickActions.LooksValid(token) && token.Length == 43, "Tokens are long and URL-safe.");
    AssertFalse(QuickActions.LooksValid("short") || QuickActions.LooksValid(new string('a', 30) + "/"), "Malformed tokens are refused before any lookup.");
    AssertTrue(await db.FindQuickActionAsync(QuickActions.Hash("x" + token), now) == null, "An unknown token does nothing.");
    AssertTrue(await db.FindQuickActionAsync(QuickActions.Hash(token), now) == ("S-1-5-21-1-2-3-1001", "req1"), "The token names the account and request it was made for.");
    AssertTrue(await db.FindQuickActionAsync(QuickActions.Hash(token), now) != null, "A token stays usable until its answer is recorded.");
    await db.MarkQuickActionUsedAsync(QuickActions.Hash(token));
    AssertTrue(await db.FindQuickActionAsync(QuickActions.Hash(token), now) == null, "A token works only once.");
    var old = await QuickActions.CreateDeclineAsync(db, "S-1-5-21-1-2-3-1001", "req2", now.AddMinutes(1));
    AssertTrue(await db.FindQuickActionAsync(QuickActions.Hash(old), now.AddMinutes(2)) == null, "An expired token does nothing.");

    var file = Path.Combine(Path.GetTempPath(), "requests-" + Guid.NewGuid().ToString("N") + ".json");
    try
    {
        var t0 = DateTimeOffset.UtcNow;
        MonitorAndControl.ScreenTime.TimeRequestStore.Add("S-1", "Tom", t0, file);
        var first = MonitorAndControl.ScreenTime.TimeRequestStore.Pending(t0, file).Single();
        MonitorAndControl.ScreenTime.TimeRequestStore.Resolve("S-1", file, first.Id);
        MonitorAndControl.ScreenTime.TimeRequestStore.Add("S-1", "Tom", t0.AddMinutes(1), file);
        AssertTrue(MonitorAndControl.ScreenTime.TimeRequestStore.Resolve("S-1", file, first.Id) == 0
            && MonitorAndControl.ScreenTime.TimeRequestStore.Pending(t0.AddMinutes(1), file).Count == 1, "Not now on an old alert leaves a newer request open.");
    }
    finally { File.Delete(file); }
}

static async Task TestPerAccountRules()
{
    const string tom = "S-1-5-21-1-2-3-1001", ann = "S-1-5-21-1-2-3-1002";
    using var db = CreateTempDatabase();
    await db.SaveLimitGroupAsync(new AppLimitGroup { Name = "Games (Tom)", DailyMaxMinutes = 60, AppNames = new() { "Minecraft" }, UserSid = tom });
    await db.SaveLimitGroupAsync(new AppLimitGroup { Name = "Games (Ann)", DailyMaxMinutes = 90, AppNames = new() { "Minecraft" }, UserSid = ann });
    var groups = await db.GetLimitGroupsAsync();
    AssertTrue(groups.Count == 2 && groups.Single(g => g.Name == "Games (Tom)").UserSid == tom, "Two accounts can each have a group with the same app.");

    await db.SaveScheduleRuleAsync(new ScheduleRule { DayOfWeek = "Everyday", StartTime = "15:00", EndTime = "20:00", UserSid = ann });
    AssertTrue((await db.GetScheduleRulesAsync()).Single().UserSid == ann, "A schedule rule keeps its account.");
    AssertFalse(SchedulerService.AppliesHere(new ScheduleRule { UserSid = "S-1-5-21-9-9-9-9999" }), "A rule for another account is ignored here.");
    AssertTrue(SchedulerService.AppliesHere(new ScheduleRule()), "A rule for everyone applies here.");

    await db.SaveUserSiteLimitAsync(new SiteLimit { Host = "youtube.com", DailyMaxMinutes = 30, UserSid = tom });
    await db.SaveSiteLimitAsync(new SiteLimit { Host = "tiktok.com", DailyMaxMinutes = 0 });
    await db.AddSiteUsageAsync(MonitorAndControl.ScreenTime.ParentClock.Today, new Dictionary<string, long> { ["m.youtube.com"] = 1900 }, tom);
    await db.AddSiteUsageAsync(MonitorAndControl.ScreenTime.ParentClock.Today, new Dictionary<string, long> { ["youtube.com"] = 50 }, ann);
    var tomStatus = SiteLimitLogic.Evaluate(await db.GetUserSiteLimitsAsync(tom), await db.GetUserSiteUsageTodayAsync(tom));
    AssertTrue(tomStatus.Single().Blocked && tomStatus.Single().UsedSeconds == 1900, "An account's website allowance counts only its own browsing.");
    AssertTrue((await db.GetUserSiteUsageTodayAsync(ann)).Single().Seconds == 50, "Each account's browsing is kept apart.");

    var snapshot = await db.ReadLimitsSnapshotAsync();
    AssertTrue(snapshot.Sites.Any(s => s.Host == "youtube.com" && s.User == tom) && snapshot.Sites.Any(s => s.Host == "tiktok.com" && s.User == "")
        && snapshot.Schedule.Single().User == ann && snapshot.Groups.All(g => g.User.Length > 0), "The sealed copy keeps which account each rule is for.");
    using var other = CreateTempDatabase();
    await other.ApplyLimitsSnapshotAsync(snapshot);
    AssertTrue((await other.ReadLimitsSnapshotAsync()).Fingerprint() == snapshot.Fingerprint(), "Restoring the sealed copy brings the account rules back exactly.");

    var plain = new MonitorAndControl.ScreenTime.SealedLimits { Sites = { new() { Host = "a.com", Enabled = true } } };
    AssertTrue(plain.Fingerprint() == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("W|a.com|0|1"))),
        "Rules for everyone hash exactly as before, so limits sealed by an older version still match.");

    var sealedContent = new MonitorAndControl.ScreenTime.SealedLimits { Sites = { new() { Host = "games.com", Enabled = true, User = tom }, new() { Host = "tiktok.com", Enabled = true } } };
    var forTom = MonitorAndControl.ScreenTime.SiteBlocking.ComputePlan(Array.Empty<string>(), Array.Empty<string>(), false, false, sealedContent, activeSids: new[] { tom });
    var forAnn = MonitorAndControl.ScreenTime.SiteBlocking.ComputePlan(Array.Empty<string>(), Array.Empty<string>(), false, false, sealedContent, activeSids: new[] { ann });
    var forBoth = MonitorAndControl.ScreenTime.SiteBlocking.ComputePlan(Array.Empty<string>(), Array.Empty<string>(), false, false, sealedContent, activeSids: new[] { ann, tom });
    AssertTrue(forBoth.BlockHosts.Contains("games.com"), "With two accounts signed in at once, both accounts' rules apply.");
    AssertTrue(forTom.BlockHosts.Contains("games.com") && forTom.BlockHosts.Contains("tiktok.com"), "Tom at the screen: his sealed block and everyone's apply.");
    AssertTrue(!forAnn.BlockHosts.Contains("games.com") && forAnn.BlockHosts.Contains("tiktok.com"), "Ann at the screen: Tom's rule is not in force.");
}

static async Task TestGroupMembersUpgrade()
{
    var path = Path.Combine(Path.GetTempPath(), "MonitorAndControlTests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    await using (var connection = new SqliteConnection($"Data Source={path}"))
    {
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE LimitGroups (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL UNIQUE, DailyMaxMinutes INTEGER NOT NULL DEFAULT 180, Enabled INTEGER NOT NULL DEFAULT 1);
            CREATE TABLE LimitGroupMembers (GroupId INTEGER NOT NULL, AppName TEXT NOT NULL UNIQUE, PRIMARY KEY (GroupId, AppName), FOREIGN KEY (GroupId) REFERENCES LimitGroups(Id) ON DELETE CASCADE);
            INSERT INTO LimitGroups (Name, DailyMaxMinutes) VALUES ('Games', 60);
            INSERT INTO LimitGroupMembers (GroupId, AppName) VALUES (1, 'Minecraft');
            """;
        await command.ExecuteNonQueryAsync();
    }
    SqliteConnection.ClearAllPools();
    using var db = new UsageDatabase(path);
    var old = (await db.GetLimitGroupsAsync()).Single();
    AssertTrue(old.Name == "Games" && old.UserSid == "" && old.AppNames.Single() == "Minecraft", "Existing groups survive the upgrade and apply to everyone.");
    await db.SaveLimitGroupAsync(new AppLimitGroup { Name = "Games (Tom)", DailyMaxMinutes = 30, AppNames = new() { "Minecraft" }, UserSid = "S-1-5-21-1-2-3-1001" });
    AssertTrue((await db.GetLimitGroupsAsync()).Count == 2, "After the upgrade an app can be in a group for one account too.");
}
