namespace MonitorAndControl.Models;

/// <summary>What one signed-in Windows account is doing, as reported by its own DeviceMon.</summary>
public record LiveSession(string UserSid, string Account, string App, string Process, string State);
