using System.Text.Json;

namespace MonitorAndControl.ScreenTime;

/// <summary>A child's "please give me more PC time" request, written by the SYSTEM service.</summary>
public sealed class TimeRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string UserSid { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public bool Resolved { get; set; }
}

public static class TimeRequestStore
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);
    private const int MaxEntries = 50;

    public static string Path => System.IO.Path.Combine(ScreenTimeFiles.DirectoryPath, "time-requests.json");

    public static List<TimeRequest> Read(string? path = null)
    {
        path ??= Path;
        try
        {
            if (!File.Exists(path)) return new();
            return JsonSerializer.Deserialize<List<TimeRequest>>(File.ReadAllText(path)) ?? new();
        }
        catch { return new(); }
    }

    public static List<TimeRequest> Pending(DateTimeOffset now, string? path = null) =>
        Read(path).Where(r => !r.Resolved && now - r.CreatedUtc < Lifetime).OrderBy(r => r.CreatedUtc).ToList();

    /// <summary>Adds a request unless the account already has one waiting. Returns false for a duplicate.</summary>
    public static bool Add(string sid, string name, DateTimeOffset now, string? path = null)
    {
        path ??= Path;
        var all = Read(path).Where(r => now - r.CreatedUtc < TimeSpan.FromDays(2)).ToList();
        if (all.Any(r => r.UserSid == sid && !r.Resolved && now - r.CreatedUtc < Lifetime)) return false;
        all.Add(new TimeRequest { UserSid = sid, Name = name, CreatedUtc = now });
        ScreenTimeFiles.WriteAtomic(path, all.OrderBy(r => r.CreatedUtc).TakeLast(MaxEntries).ToList());
        return true;
    }

    /// <summary>Marks every waiting request from this account as answered.</summary>
    public static int Resolve(string sid, string? path = null)
    {
        path ??= Path;
        var all = Read(path);
        var count = 0;
        foreach (var r in all.Where(r => r.UserSid == sid && !r.Resolved)) { r.Resolved = true; count++; }
        if (count > 0) ScreenTimeFiles.WriteAtomic(path, all);
        return count;
    }
}
