using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MonitorAndControl.ScreenTime;

public sealed class SealedLimit
{
    public string App { get; set; } = "";
    public int Minutes { get; set; }
    public bool Enabled { get; set; }
}

public sealed class SealedSchedule
{
    public string App { get; set; } = "";
    public string Day { get; set; } = "";
    public string Start { get; set; } = "";
    public string End { get; set; } = "";
    public bool Enabled { get; set; }
    /// <summary>Empty = everyone; otherwise only this Windows account (SID).</summary>
    public string User { get; set; } = "";
}

public sealed class SealedGroup
{
    public string Name { get; set; } = "";
    public int Minutes { get; set; }
    public bool Enabled { get; set; }
    public List<string> Members { get; set; } = new();
    /// <summary>Empty = everyone; otherwise only this Windows account (SID).</summary>
    public string User { get; set; } = "";
}

public sealed class SealedUserLimit
{
    public string User { get; set; } = "";
    public string App { get; set; } = "";
    public int Minutes { get; set; }
    public bool Enabled { get; set; }
}

/// <summary>The app limits, allowed-hours schedule and shared groups that the parent has "sealed".</summary>
public sealed class SealedLimits
{
    public const int MaxEntries = 500;
    /// <summary>Website rules for everyone plus each account's own, so they get more room.</summary>
    public const int MaxSites = 2000;
    public List<SealedLimit> Limits { get; set; } = new();
    public List<SealedSchedule> Schedule { get; set; } = new();
    public List<SealedGroup> Groups { get; set; } = new();
    public List<SealedSite> Sites { get; set; } = new();
    /// <summary>App limits that apply to one Windows account only.</summary>
    public List<SealedUserLimit> UserLimits { get; set; } = new();
    /// <summary>Only sites on the allowed list may open.</summary>
    public bool AllowOnly { get; set; }
    /// <summary>SafeSearch and YouTube Restricted Mode are forced in the browsers.</summary>
    public bool SafeBrowsing { get; set; }
    /// <summary>Apps that are closed while homework mode is on.</summary>
    public List<string> HomeworkApps { get; set; } = new();
    /// <summary>Apps that are never closed: not by the schedule, limits, homework mode, Block now or new-program checks.</summary>
    public List<string> AlwaysAllowed { get; set; } = new();

    /// <summary>Replaces any list that arrived as null, so later code can count and hash the content safely.</summary>
    public SealedLimits Normalized()
    {
        Limits ??= new();
        Schedule ??= new();
        Groups ??= new();
        Sites ??= new();
        HomeworkApps ??= new();
        UserLimits ??= new();
        UserLimits.RemoveAll(x => x == null);
        foreach (var x in UserLimits) { x.User ??= ""; x.App ??= ""; }
        HomeworkApps.RemoveAll(x => x == null);
        AlwaysAllowed ??= new();
        AlwaysAllowed.RemoveAll(x => x == null);
        Limits.RemoveAll(x => x == null);
        Schedule.RemoveAll(x => x == null);
        Groups.RemoveAll(x => x == null);
        Sites.RemoveAll(x => x == null);
        foreach (var g in Groups) { g.Members ??= new(); g.Members.RemoveAll(m => m == null); g.Name ??= ""; g.User ??= ""; }
        foreach (var x in Limits) x.App ??= "";
        foreach (var x in Schedule) { x.App ??= ""; x.Day ??= ""; x.Start ??= ""; x.End ??= ""; x.User ??= ""; }
        foreach (var x in Sites) { x.Host ??= ""; x.User ??= ""; }
        return this;
    }

    public bool IsReasonable() =>
        Limits.Count <= MaxEntries && Schedule.Count <= MaxEntries && Groups.Count <= MaxEntries && Sites.Count <= MaxSites && HomeworkApps.Count <= MaxEntries && AlwaysAllowed.Count <= MaxEntries && UserLimits.Count <= MaxEntries
        && Groups.All(g => g.Members.Count <= MaxEntries);

    // An account is added only when set, so copies sealed before per-account rules existed keep their fingerprint.
    private static string For(string? user) => string.IsNullOrEmpty(user) ? "" : "|u:" + user;

    /// <summary>Order-independent hash of the content, so two copies compare equal exactly when they mean the same.</summary>
    public string Fingerprint()
    {
        var lines = Limits.Select(l => $"L|{l.App}|{l.Minutes}|{(l.Enabled ? 1 : 0)}")
            .Concat(Schedule.Select(s => $"S|{s.App}|{s.Day}|{s.Start}|{s.End}|{(s.Enabled ? 1 : 0)}{For(s.User)}"))
            .Concat(Sites.Select(w => $"W|{w.Host}|{w.Minutes}|{(w.Enabled ? 1 : 0)}{For(w.User)}"))
            .Concat(UserLimits.Select(u => $"U|{u.User}|{u.App}|{u.Minutes}|{(u.Enabled ? 1 : 0)}"))
            .Concat(HomeworkApps.Select(h => h.ToLowerInvariant()).Distinct().Select(h => $"H|{h}"))
            .Concat(AlwaysAllowed.Select(a => a.ToLowerInvariant()).Distinct().Select(a => $"A|{a}"))
            .Concat(AllowOnly || SafeBrowsing ? new[] { $"P|{(AllowOnly ? 1 : 0)}|{(SafeBrowsing ? 1 : 0)}" } : Array.Empty<string>())
            .Concat(Groups.Select(g => $"G|{g.Name}|{g.Minutes}|{(g.Enabled ? 1 : 0)}|" +
                string.Join(',', g.Members.OrderBy(m => m, StringComparer.OrdinalIgnoreCase)) + For(g.User)))
            .OrderBy(x => x, StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(';', lines))));
    }
}

public enum SealPhase
{
    /// <summary>Nothing is sealed; the in-process guard is the only protection.</summary>
    Off,
    /// <summary>Sealed: the database must match the sealed copy.</summary>
    Enforce,
    /// <summary>The parent approved editing; changes are allowed until the window closes.</summary>
    Editing,
    /// <summary>The window just closed; DeviceMon should send the new content to be sealed.</summary>
    SealNow
}

/// <summary>
/// State stored by the SYSTEM service, readable but not writable by the child. While sealed, the limits
/// can only change after the parent approves an edit window (UAC on the PC, the remote PIN elsewhere).
/// </summary>
public sealed class LimitsSealFile
{
    public bool Protected { get; set; }
    public SealedLimits? Content { get; set; }
    public string Fingerprint { get; set; } = "";
    public DateTimeOffset? SealedUtc { get; set; }
    public bool PendingSeal { get; set; }
    public DateTimeOffset? OpenUntilUtc { get; set; }
    public DateTimeOffset? SealableUntilUtc { get; set; }
}

public static class LimitsSealStore
{
    /// <summary>How long after an edit window ends DeviceMon may still deliver the new content.</summary>
    public static readonly TimeSpan SealGrace = TimeSpan.FromMinutes(2);
    public const int MaxWindowMinutes = 60;

    public static string Path => System.IO.Path.Combine(ScreenTimeFiles.DirectoryPath, "limits-seal.json");

    public static LimitsSealFile? Read(string? path = null)
    {
        try
        {
            path ??= Path;
            return File.Exists(path) ? JsonSerializer.Deserialize<LimitsSealFile>(File.ReadAllText(path)) : null;
        }
        catch { return null; }
    }

    public static void Write(LimitsSealFile file, string? path = null) => ScreenTimeFiles.WriteAtomic(path ?? Path, file);

    public static void Clear(string? path = null)
    {
        path ??= Path;
        foreach (var f in new[] { path, path + ".bak" })
            try { if (File.Exists(f)) File.Delete(f); } catch { }
    }

    public static SealPhase Phase(LimitsSealFile? file, DateTimeOffset now)
    {
        if (file == null) return SealPhase.Off;
        if (file.PendingSeal)
        {
            if (file.OpenUntilUtc is { } open && now < open) return SealPhase.Editing;
            if (file.SealableUntilUtc is { } grace && now <= grace) return SealPhase.SealNow;
        }
        return file.Protected && file.Content != null ? SealPhase.Enforce : SealPhase.Off;
    }

    /// <summary>Starts an edit window. The previous sealed content keeps protecting until new content is sealed.</summary>
    public static LimitsSealFile Open(LimitsSealFile? current, int minutes, DateTimeOffset now)
    {
        minutes = Math.Clamp(minutes, 1, MaxWindowMinutes);
        return new LimitsSealFile
        {
            Protected = current?.Protected ?? false,
            Content = current?.Content,
            Fingerprint = current?.Fingerprint ?? "",
            SealedUtc = current?.SealedUtc,
            PendingSeal = true,
            OpenUntilUtc = now.AddMinutes(minutes),
            SealableUntilUtc = now.AddMinutes(minutes) + SealGrace
        };
    }

    /// <summary>Ends the window early; DeviceMon then has the grace period to seal the result.</summary>
    public static LimitsSealFile Close(LimitsSealFile current, DateTimeOffset now)
    {
        if (!current.PendingSeal || Phase(current, now) != SealPhase.Editing) return current;
        current.OpenUntilUtc = now;
        current.SealableUntilUtc = now + SealGrace;
        return current;
    }

    /// <summary>Accepts new content only from a window the parent opened (still inside its grace period).</summary>
    public static bool TrySeal(LimitsSealFile? current, SealedLimits content, DateTimeOffset now, out LimitsSealFile next)
    {
        next = current ?? new LimitsSealFile();
        if (current is not { PendingSeal: true } || current.SealableUntilUtc is not { } until || now > until
            || current.OpenUntilUtc is not { } opened || now < opened || !content.IsReasonable())
            return false;
        next = new LimitsSealFile
        {
            Protected = true,
            Content = content,
            Fingerprint = content.Fingerprint(),
            SealedUtc = now,
            PendingSeal = false
        };
        return true;
    }
}
