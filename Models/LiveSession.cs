namespace MonitorAndControl.Models;

public sealed record LiveSessionRecord(
    int SessionId,
    string? UserSid,
    string? UserName,
    string? AppName,
    string? ProcessName,
    bool IsTracking,
    long IdleSeconds,
    DateTime LastSeenUtc);
