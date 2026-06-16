using MonitorAndControl.Data;
using MonitorAndControl.Models;
using MonitorAndControl.Services;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Scheduler allows time inside same-day window", TestSchedulerInsideSameDayWindow),
    ("Scheduler blocks time outside same-day window", TestSchedulerOutsideSameDayWindow),
    ("Scheduler handles overnight windows", TestSchedulerOvernightWindow),
    ("Scheduler finds next allowed time", TestSchedulerNextAllowedTime),
    ("Usage database accumulates daily app usage", TestUsageDatabaseAccumulatesUsage),
    ("Usage database clears today's usage", TestUsageDatabaseClearsToday),
    ("Usage database tracks and clears daily bonus time", TestUsageDatabaseTracksBonusTime),
    ("Usage database stores per-app schedule targets", TestUsageDatabaseStoresScheduleTarget),
    ("Usage database replaces backup-managed config tables", TestUsageDatabaseReplacesConfigTables),
    ("Limit enforcer rehydrates exceeded apps", TestLimitEnforcerRehydratesExceededApps),
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

static async Task TestUsageDatabaseAccumulatesUsage()
{
    using var db = CreateTempDatabase();

    await db.RecordUsageAsync("Chess", "chess.exe", 30);
    await db.RecordUsageAsync("Chess", "chess.exe", 45);

    var today = await db.GetTodayUsageAsync();
    var record = today.SingleOrDefault(x => x.AppName == "Chess");

    AssertTrue(record != null, "Expected a usage record for Chess.");
    AssertEqual(75L, record!.TotalSeconds, "Usage seconds should accumulate.");
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

static async Task TestUsageDatabaseReplacesConfigTables()
{
    using var db = CreateTempDatabase();

    await db.SaveAppMappingAsync("old.exe", "Old");
    await db.SaveLimitRuleAsync(new AppLimitRule { AppName = "Old", DailyMaxMinutes = 30, Enabled = true });
    await db.SaveScheduleRuleAsync(new ScheduleRule { AppName = "Old", DayOfWeek = "Everyday", StartTime = "10:00", EndTime = "11:00", Enabled = true });

    await db.ReplaceAppMappingsAsync(new[] { new AppMapping("new.exe", "New") });
    await db.ReplaceLimitRulesAsync(new[] { new AppLimitRule { AppName = "New", DailyMaxMinutes = 60, Enabled = false } });
    await db.ReplaceScheduleRulesAsync(new[] { new ScheduleRule { AppName = "New", DayOfWeek = "Weekend", StartTime = "12:00", EndTime = "13:00", Enabled = true } });

    var mappings = await db.GetAppMappingsAsync();
    var limits = await db.GetLimitRulesAsync();
    var schedules = await db.GetScheduleRulesAsync();

    AssertEqual(1, mappings.Count, "Expected one mapping after replace.");
    AssertEqual("New", mappings[0].AppName, "Expected replacement mapping.");
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

static ScheduleRule Rule(string day, string start, string end) => new()
{
    DayOfWeek = day,
    StartTime = start,
    EndTime = end,
    Enabled = true
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
