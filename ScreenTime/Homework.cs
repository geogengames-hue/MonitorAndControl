using System.Text.Json;

namespace MonitorAndControl.ScreenTime;

/// <summary>
/// "Homework mode": for a set time, chosen apps are closed and only the allowed websites open. The end time is
/// kept by the SYSTEM service where the child cannot change it; only the parent's approval can end it early.
/// </summary>
public sealed class HomeworkState
{
    public const int MinMinutes = 10;
    public const int MaxMinutes = 240;

    public DateTimeOffset? UntilUtc { get; set; }

    public static string Path => System.IO.Path.Combine(ScreenTimeFiles.DirectoryPath, "homework.json");

    public static HomeworkState? Read(string? path = null)
    {
        try
        {
            path ??= Path;
            return File.Exists(path) ? JsonSerializer.Deserialize<HomeworkState>(File.ReadAllText(path)) : null;
        }
        catch { return null; }
    }

    public static bool IsActive(HomeworkState? state, DateTimeOffset now) => state?.UntilUtc is { } until && until > now;

    public static bool IsActiveNow() => IsActive(Read(), DateTimeOffset.UtcNow);

    public static HomeworkState Start(int minutes, DateTimeOffset now) =>
        new() { UntilUtc = now.AddMinutes(Math.Clamp(minutes, MinMinutes, MaxMinutes)) };

    public static void Write(HomeworkState state, string? path = null) => ScreenTimeFiles.WriteAtomic(path ?? Path, state);

    public static void Clear(string? path = null)
    {
        path ??= Path;
        foreach (var f in new[] { path, path + ".bak" })
            try { if (File.Exists(f)) File.Delete(f); } catch { }
    }
}
