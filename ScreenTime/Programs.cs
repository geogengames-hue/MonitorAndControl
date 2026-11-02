using System.Text.Json;
using System.Text.RegularExpressions;

namespace MonitorAndControl.ScreenTime;

/// <summary>A program seen on the PC: allowed (already there, or allowed by the parent), waiting for an answer, or blocked.</summary>
public sealed class ProgramEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>The process name without ".exe", in lower case.</summary>
    public string Name { get; set; } = "";
    /// <summary>A friendlier name from the program file, when it has one.</summary>
    public string Display { get; set; } = "";
    public string State { get; set; } = ProgramApprovals.Allowed;
    /// <summary>True once the parent answered (as opposed to programs that were already on the PC).</summary>
    public bool Decided { get; set; }
    public string UserSid { get; set; } = "";
    public string Account { get; set; } = "";
    public DateTimeOffset SeenUtc { get; set; }
}

/// <summary>Kept by the SYSTEM service: readable by everyone, written only by the service.</summary>
public sealed class ProgramsState
{
    public bool Enabled { get; set; }
    /// <summary>When it was last switched on; a program file newer than this is never recorded as "already there".</summary>
    public DateTimeOffset? EnabledUtc { get; set; }
    /// <summary>Accounts whose already-installed programs were recorded as allowed when this was turned on.</summary>
    public List<string> Baselined { get; set; } = new();
    public List<ProgramEntry> Entries { get; set; } = new();
}

/// <summary>
/// "New programs need my OK": programs that were not on the PC when the parent turned this on are closed
/// until the parent allows them. The list lives with the SYSTEM service, so the child cannot approve anything;
/// allowing a program needs the parent's approval (UAC on the PC, the remote PIN elsewhere).
/// </summary>
public static class ProgramApprovals
{
    public const string Allowed = "allowed";
    public const string Waiting = "waiting";
    public const string Blocked = "blocked";
    public const int MaxEntries = 5000;
    public const int MaxBaseline = 3000;
    private const int MaxOpen = 100;
    private static readonly Regex ValidName = new(@"^[\p{L}\p{N} ._()+\-]{1,100}$", RegexOptions.Compiled);

    public static string Path => System.IO.Path.Combine(ScreenTimeFiles.DirectoryPath, "programs.json");

    public static ProgramsState Read(string? path = null)
    {
        try
        {
            path ??= Path;
            var state = File.Exists(path) ? JsonSerializer.Deserialize<ProgramsState>(File.ReadAllText(path)) : null;
            state ??= new();
            state.Baselined ??= new();
            state.Entries ??= new();
            state.Entries.RemoveAll(e => e == null || string.IsNullOrEmpty(e.Name));
            return state;
        }
        catch { return new(); }
    }

    public static void Write(ProgramsState state, string? path = null) => ScreenTimeFiles.WriteAtomic(path ?? Path, state);

    /// <summary>
    /// The name a program is known by: the original file name built into the program file when it has one (renaming
    /// game.exe to chrome.exe does not change it), otherwise its process name.
    /// </summary>
    public static string? Identity(string processName, string? originalFileName) =>
        Key(System.IO.Path.GetFileName(originalFileName ?? "")) ?? Key(processName);

    /// <summary>"Minecraft.exe" and "minecraft" are the same program; null for names that cannot be a program.</summary>
    public static string? Key(string? processName)
    {
        var name = (processName ?? "").Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        name = name.ToLowerInvariant();
        return ValidName.IsMatch(name) ? name : null;
    }

    public static ProgramEntry? Find(ProgramsState state, string key) => state.Entries.FirstOrDefault(e => e.Name == key);

    /// <summary>May this program run? Everything may while the switch is off.</summary>
    public static bool IsAllowed(ProgramsState state, string key) => !state.Enabled || Find(state, key)?.State == Allowed;

    /// <summary>Turning it on only restricts. Each account records what it already has again, so nothing old is blocked.</summary>
    public static void Enable(ProgramsState state, DateTimeOffset now)
    {
        if (!state.Enabled) { state.Baselined.Clear(); state.EnabledUtc = now; }
        state.Enabled = true;
    }

    /// <summary>Records the programs an account already has as allowed. Each account does this once per switch-on.</summary>
    public static bool Baseline(ProgramsState state, string sid, IEnumerable<string> names, DateTimeOffset now)
    {
        if (!state.Enabled || string.IsNullOrEmpty(sid) || state.Baselined.Contains(sid)) return false;
        foreach (var key in names.Select(Key).Where(k => k != null).Distinct().Take(MaxBaseline))
            if (Find(state, key!) == null) state.Entries.Add(new ProgramEntry { Name = key!, State = Allowed, SeenUtc = now });
        state.Baselined.Add(sid);
        Trim(state);
        return true;
    }

    /// <summary>A program that is not allowed was opened. Returns its entry and whether the parent still has to be told.</summary>
    public static (ProgramEntry Entry, bool IsNew)? Request(ProgramsState state, string key, string display, string sid, string account, DateTimeOffset now)
    {
        if (Find(state, key) is { } existing) return (existing, false);
        if (state.Entries.Count(e => e.State == Waiting) >= MaxOpen) return null;
        var entry = new ProgramEntry
        {
            Name = key, Display = Clean(display), State = Waiting, UserSid = sid, Account = Clean(account), SeenUtc = now
        };
        state.Entries.Add(entry);
        Trim(state);
        return (entry, true);
    }

    /// <summary>The parent's answer. Allowing goes through approval; keeping one blocked does not need it.</summary>
    public static ProgramEntry? Decide(ProgramsState state, string key, bool allow)
    {
        var entry = Find(state, key);
        if (entry == null) return null;
        entry.State = allow ? Allowed : Blocked;
        entry.Decided = true;
        return entry;
    }

    /// <summary>What the parent sees: everything waiting or blocked, and what they allowed themselves.</summary>
    public static IEnumerable<ProgramEntry> Visible(ProgramsState state) =>
        state.Entries.Where(e => e.State != Allowed || e.Decided).OrderByDescending(e => e.State == Waiting).ThenByDescending(e => e.SeenUtc);

    private static string Clean(string? text)
    {
        var value = new string((text ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return value.Length > 80 ? value[..80] : value;
    }

    // Programs already on the PC are dropped first; the parent's answers are kept.
    private static void Trim(ProgramsState state)
    {
        var extra = state.Entries.Count - MaxEntries;
        if (extra <= 0) return;
        var drop = state.Entries.Where(e => e.State == Allowed && !e.Decided).Take(extra).ToHashSet();
        state.Entries.RemoveAll(drop.Contains);
    }
}
