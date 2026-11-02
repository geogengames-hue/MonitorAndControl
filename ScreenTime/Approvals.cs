namespace MonitorAndControl.ScreenTime;

public sealed record ScreenTimeChange(string UserSid, int DailyMinutes, int BonusMinutes, bool Enabled, int WeekendMinutes = 0,
    string BedtimeStart = "", string BedtimeEnd = "", string WeekendBedtimeStart = "", int LockMinutes = 0,
    string Operation = "", int LimitsMinutes = 0, string TimeZoneId = "", string ProgramName = "",
    int BreakAfterMinutes = 0, int BreakMinutes = 0);

public sealed class ScreenTimeApprovals
{
    private sealed class Entry
    {
        public required string CallerSid { get; init; }
        public required ScreenTimeChange Change { get; init; }
        public required DateTimeOffset Expires { get; init; }
        public bool Claimed { get; set; }
        public ScreenTimeReply? Result { get; set; }
    }
    private readonly Dictionary<string, Entry> _entries = new();
    public string Create(string callerSid, ScreenTimeChange change, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(callerSid)) throw new UnauthorizedAccessException();
        foreach (var expired in _entries.Where(e => e.Value.Expires <= now).Select(e => e.Key).ToArray()) _entries.Remove(expired);
        if (_entries.Count >= 32 || _entries.Values.Count(e => e.CallerSid == callerSid && !e.Claimed) >= 4)
            throw new InvalidOperationException("Too many changes are waiting for authorization. Try again in a moment.");
        var id = Guid.NewGuid().ToString("N");
        _entries.Add(id, new Entry { CallerSid = callerSid, Change = change, Expires = now.AddMinutes(2) });
        return id;
    }
    public bool TryClaim(string id, bool elevatedAdministrator, DateTimeOffset now, out ScreenTimeChange? change)
    {
        change = null;
        if (!elevatedAdministrator || !_entries.TryGetValue(id, out var entry)
            || entry.Expires <= now || entry.Claimed) return false;
        entry.Claimed = true;
        change = entry.Change;
        return true;
    }
    public void Complete(string id, ScreenTimeReply result)
    {
        if (!_entries.TryGetValue(id, out var entry) || !entry.Claimed || entry.Result != null)
            throw new InvalidOperationException("Invalid authorization.");
        entry.Result = result;
    }
    public ScreenTimeReply Result(string id, string callerSid, DateTimeOffset now)
    {
        if (!_entries.TryGetValue(id, out var entry) || entry.CallerSid != callerSid || entry.Expires <= now)
            return ScreenTimeReply.Failure("Authorization expired or not recognized.");
        return entry.Result ?? ScreenTimeReply.Failure("The change has not been authorized in Windows yet.");
    }
    public void Cancel(string id, string callerSid)
    {
        if (_entries.TryGetValue(id, out var entry) && entry.CallerSid == callerSid && !entry.Claimed)
            _entries.Remove(id);
    }
}
