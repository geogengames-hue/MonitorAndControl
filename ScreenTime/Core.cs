using System.Text.Json;

namespace MonitorAndControl.ScreenTime;

public sealed class ScreenTimePolicy
{
    public string ProfileId { get; set; } = Guid.NewGuid().ToString("N");
    public string UserSid { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int DailyMinutes { get; set; } = 180;
    /// <summary>Saturday and Sunday allowance; 0 means the same as every other day.</summary>
    public int WeekendMinutes { get; set; }
    /// <summary>Bedtime: the account is signed out from BedtimeStart until BedtimeEnd ("HH:mm"; empty = no bedtime).</summary>
    public string BedtimeStart { get; set; } = "";
    public string BedtimeEnd { get; set; } = "";
    /// <summary>Optional later bedtime for nights before a day off (Friday and Saturday nights); empty = same as BedtimeStart.</summary>
    public string WeekendBedtimeStart { get; set; } = "";
    /// <summary>A parent's "lock now": the account is kept at the lock screen until this moment.</summary>
    public DateTimeOffset? LockUntilUtc { get; set; }
    public bool Enabled { get; set; } = true;
    public string BonusDay { get; set; } = "";
    public int BonusMinutes { get; set; }
    /// <summary>Break reminders: after this many minutes of use in a row the account is locked for BreakMinutes (0 = off).</summary>
    public int BreakAfterMinutes { get; set; }
    public int BreakMinutes { get; set; }
    public void Validate()
    {
        if (!Guid.TryParseExact(ProfileId, "N", out _) || !UserSid.StartsWith("S-1-5-21-", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Length > 100
            || DailyMinutes is < 1 or > 1440 || WeekendMinutes is < 0 or > 1440 || !ScreenTimeRules.ValidBedtime(this) || BonusMinutes is < 0 or > 1440
            || !ScreenTimeRules.ValidBreak(BreakAfterMinutes, BreakMinutes))
            throw new InvalidDataException("Invalid screen-time policy.");
    }
}

public sealed class ScreenTimeUsage
{
    public string Day { get; set; } = "";
    public double Seconds { get; set; }
    /// <summary>Break reminders: use in a row since the last break, when it was last used, and the break in progress.</summary>
    public double RunSeconds { get; set; }
    public DateTimeOffset? LastActiveUtc { get; set; }
    public DateTimeOffset? BreakUntilUtc { get; set; }
    public int RunWarned { get; set; }
}

public static class ScreenTimeRules
{
    public static bool TryClock(string? text, out TimeSpan time)
    {
        time = default;
        if (string.IsNullOrEmpty(text) || text.Length != 5 || text[2] != ':'
            || !int.TryParse(text.AsSpan(0, 2), out var h) || !int.TryParse(text.AsSpan(3, 2), out var m)
            || h is < 0 or > 23 || m is < 0 or > 59) return false;
        time = new TimeSpan(h, m, 0);
        return true;
    }

    public static bool ValidBedtime(ScreenTimePolicy p)
    {
        var any = !string.IsNullOrEmpty(p.BedtimeStart) || !string.IsNullOrEmpty(p.BedtimeEnd) || !string.IsNullOrEmpty(p.WeekendBedtimeStart);
        if (!any) return true;
        if (!TryClock(p.BedtimeStart, out var s) || !TryClock(p.BedtimeEnd, out var e) || s == e) return false;
        if (string.IsNullOrEmpty(p.WeekendBedtimeStart)) return true;
        // An overnight bedtime (after the wake-up time) needs an overnight weekend bedtime too; 00:30 would read as a daytime window.
        return TryClock(p.WeekendBedtimeStart, out var w) && w != e && (s > e) == (w > e);
    }

    // The night that starts on a Friday or Saturday may use the later weekend bedtime.
    private static TimeSpan? BedtimeStartFor(ScreenTimePolicy p, DayOfWeek nightStartsOn)
    {
        if (!TryClock(p.BedtimeStart, out var normal)) return null;
        if (nightStartsOn is DayOfWeek.Friday or DayOfWeek.Saturday && TryClock(p.WeekendBedtimeStart, out var weekend)) return weekend;
        return normal;
    }

    public static bool InBedtime(ScreenTimePolicy p, DateTime now)
    {
        if (!TryClock(p.BedtimeEnd, out var end)) return false;
        var t = now.TimeOfDay;
        if (BedtimeStartFor(p, now.DayOfWeek) is { } today)
        {
            if (today > end) { if (t >= today) return true; }
            else if (t >= today && t < end) return true;
        }
        // A night that began yesterday and runs past midnight.
        return BedtimeStartFor(p, now.AddDays(-1).DayOfWeek) is { } yesterday && yesterday > end && t < end;
    }

    /// <summary>Whole minutes until today's bedtime starts, or null when there is none ahead today.</summary>
    public static int? MinutesUntilBedtime(ScreenTimePolicy p, DateTime now)
    {
        if (InBedtime(p, now) || BedtimeStartFor(p, now.DayOfWeek) is not { } start) return null;
        var left = start - now.TimeOfDay;
        return left > TimeSpan.Zero ? (int)Math.Ceiling(left.TotalMinutes) : null;
    }

    public const int MaxLockMinutes = 720;

    /// <summary>Off (0 and 0), or a break of 5 to 120 minutes after 15 to 240 minutes of use in a row.</summary>
    public static bool ValidBreak(int afterMinutes, int breakMinutes) =>
        afterMinutes == 0 ? breakMinutes == 0 : afterMinutes is >= 15 and <= 240 && breakMinutes is >= 5 and <= 120;

    public static bool OnBreak(ScreenTimeUsage usage, DateTimeOffset now) => usage.BreakUntilUtc is { } until && until > now;

    /// <summary>
    /// Counts use in a row for break reminders. Not using the account (locked, signed out, asleep) for a whole break
    /// length counts as a break. Returns whether a break starts now, or the warning to show (5 or 1 minutes left, else 0).
    /// </summary>
    public static (bool StartBreak, int WarnMinutes) AdvanceBreak(ScreenTimePolicy policy, ScreenTimeUsage usage, DateTimeOffset now, double activeSeconds)
    {
        if (policy.BreakAfterMinutes <= 0) { usage.RunSeconds = 0; usage.RunWarned = 0; usage.BreakUntilUtc = null; return (false, 0); }
        if (OnBreak(usage, now) || activeSeconds <= 0 || !double.IsFinite(activeSeconds)) return (false, 0);
        if (usage.LastActiveUtc is { } last && now - last >= TimeSpan.FromMinutes(policy.BreakMinutes))
        {
            usage.RunSeconds = 0;
            usage.RunWarned = 0;
        }
        usage.LastActiveUtc = now;
        usage.RunSeconds = Math.Max(0, usage.RunSeconds) + Math.Clamp(activeSeconds, 0, 5);
        var left = policy.BreakAfterMinutes * 60.0 - usage.RunSeconds;
        // The parent shortened the time between breaks below what was already used: warn a minute ahead first.
        if (left <= 0 && usage.RunWarned != 1)
        {
            usage.RunSeconds = policy.BreakAfterMinutes * 60.0 - 60;
            usage.RunWarned = 1;
            return (false, 1);
        }
        if (left <= 0)
        {
            usage.RunSeconds = 0;
            usage.RunWarned = 0;
            usage.BreakUntilUtc = now.AddMinutes(policy.BreakMinutes);
            return (true, 0);
        }
        var warn = left <= 60 ? 1 : left <= 300 ? 5 : 0;
        if (warn == 0 || warn == usage.RunWarned) return (false, 0);
        usage.RunWarned = warn;
        return (false, warn);
    }

    public static bool IsLocked(ScreenTimePolicy policy, DateTimeOffset now) => policy.LockUntilUtc is { } until && until > now;

    /// <summary>0 leaves the lock alone, a negative value unlocks, a positive value locks for that many minutes.</summary>
    public static DateTimeOffset? ApplyLock(DateTimeOffset? current, int lockMinutes, DateTimeOffset now) =>
        lockMinutes < 0 ? null : lockMinutes == 0 ? current : now.AddMinutes(Math.Min(lockMinutes, MaxLockMinutes));

    public static int LimitFor(ScreenTimePolicy policy, DateOnly day) =>
        day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday && policy.WeekendMinutes > 0 ? policy.WeekendMinutes : policy.DailyMinutes;

    public static long Remaining(ScreenTimePolicy policy, ScreenTimeUsage usage, DateOnly today)
    {
        policy.Validate();
        var day = today.ToString("yyyy-MM-dd");
        var bonus = policy.BonusDay == day ? policy.BonusMinutes : 0;
        return Math.Max(0, (LimitFor(policy, today) + bonus) * 60L - (long)Math.Ceiling(usage.Seconds));
    }

    // A clock rollback must not grant another allowance. A later calendar day resets once.
    public static void Advance(ScreenTimeUsage usage, DateOnly today, double seconds)
    {
        var day = today.ToString("yyyy-MM-dd");
        if (string.CompareOrdinal(day, usage.Day) > 0)
        {
            usage.Day = day;
            usage.Seconds = 0;
        }
        if (!double.IsFinite(usage.Seconds) || usage.Seconds < 0)
            throw new InvalidDataException("Invalid screen-time usage.");
        usage.Seconds += double.IsFinite(seconds) ? Math.Clamp(seconds, 0, 5) : 0;
    }
}

public static class ScreenTimeFiles
{
    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DeviceMonScreenTime");
    public static string PoliciesPath => Path.Combine(DirectoryPath, "policies.json");
    public static string UsagePath => Path.Combine(DirectoryPath, "usage.json");
    public static string StatusPath => Path.Combine(DirectoryPath, "status.json");
    public static List<ScreenTimePolicy> ReadPolicies()
    {
        if (!File.Exists(PoliciesPath)) return new();
        var policies = JsonSerializer.Deserialize<List<ScreenTimePolicy>>(File.ReadAllText(PoliciesPath))
            ?? throw new InvalidDataException("Missing policies.");
        foreach (var policy in policies) policy.Validate();
        if (policies.Select(p => p.UserSid).Distinct().Count() != policies.Count
            || policies.Select(p => p.ProfileId).Distinct().Count() != policies.Count)
            throw new InvalidDataException("Duplicate screen-time account or profile.");
        return policies;
    }
    public static void WriteAtomic<T>(string path, T value)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
