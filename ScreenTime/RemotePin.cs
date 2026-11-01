using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MonitorAndControl.ScreenTime;

public enum RemotePinStatus { Ok, NotConfigured, Wrong, Locked }

public sealed record RemotePinResult(RemotePinStatus Status, string Message, DateTimeOffset? LockedUntil = null);

/// <summary>
/// The "remote PIN" that authorizes PC Time changes made from a remote dashboard, where no
/// Windows UAC prompt can be answered.
///
/// The PIN is stored only as a salted PBKDF2 hash, in a folder readable by SYSTEM and
/// administrators only (never by the monitored user), so it cannot be attacked offline. It is
/// set by an elevated command on the PC itself, not through the dashboard. Online guessing is
/// throttled globally (the caller is always the child's own DeviceMon process, so per-caller
/// limits would mean nothing): after <see cref="MaxFailuresBeforeLock"/> wrong attempts the
/// PIN is locked for 15 minutes, doubling on each further lockout up to 24 hours, and a locked
/// PIN is not even compared.
/// </summary>
/// <summary>
/// Checks that a folder really is closed to the monitored user. The remote PIN hash and its
/// lockout counters are only trustworthy inside such a folder: if the monitored user could read
/// it they could attack the hash offline, and if they could write it they could reset the lockout.
/// </summary>
public static class DirectoryProtection
{
    private static readonly string[] TrustedSids = { "S-1-5-18", "S-1-5-32-544" }; // SYSTEM, Administrators

    public static bool IsProtectedFromUsers(string directory)
    {
        try
        {
            var info = new DirectoryInfo(directory);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            var security = info.GetAccessControl();

            // Whoever owns the folder can rewrite its permissions.
            var owner = security.GetOwner(typeof(System.Security.Principal.SecurityIdentifier))?.Value;
            if (owner == null || !TrustedSids.Contains(owner)) return false;

            // Every permission that applies (explicit or inherited) must be for SYSTEM or Administrators.
            foreach (System.Security.AccessControl.FileSystemAccessRule rule in
                     security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)))
            {
                if (rule.AccessControlType != System.Security.AccessControl.AccessControlType.Allow) continue;
                if (!TrustedSids.Contains(rule.IdentityReference.Value)) return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public sealed class RemotePinStore
{
    public const int MinLength = 6;
    public const int MaxLength = 64;
    public const int MaxFailuresBeforeLock = 5;
    private const int DefaultIterations = 210_000;
    private static readonly TimeSpan FirstLock = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaxLock = TimeSpan.FromHours(24);

    private sealed class Record
    {
        public int Version { get; set; } = 1;
        public string Salt { get; set; } = "";
        public string Hash { get; set; } = "";
        public int Iterations { get; set; } = DefaultIterations;
        public int Failures { get; set; }
        public int Locks { get; set; }
        /// <summary>Counts every lockout ever triggered (never reset by a correct PIN), so the host can alert on each new one.</summary>
        public int TotalLockouts { get; set; }
        public DateTimeOffset? LockedUntil { get; set; }
    }

    private readonly string _path;
    private readonly object _sync = new();

    public RemotePinStore(string path) => _path = path;

    public static bool IsAcceptable(string? pin, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(pin) || pin.Length < MinLength || pin.Length > MaxLength)
            error = $"The PIN must be {MinLength}-{MaxLength} characters.";
        else if (pin.Distinct().Count() < 3)
            error = "The PIN is too simple. Use at least three different characters.";
        return error.Length == 0;
    }

    /// <summary>Sets (or replaces) the PIN and clears any lockout.</summary>
    public void Set(string pin)
    {
        if (!IsAcceptable(pin, out var error)) throw new ArgumentException(error);
        var salt = RandomNumberGenerator.GetBytes(16);
        Save(new Record { Salt = Convert.ToBase64String(salt), Hash = Convert.ToBase64String(Hash(pin, salt, DefaultIterations)) });
    }

    public void Clear()
    {
        lock (_sync)
        {
            if (File.Exists(_path)) File.Delete(_path);
            if (File.Exists(_path + ".bak")) File.Delete(_path + ".bak");
        }
    }

    public (bool Configured, DateTimeOffset? LockedUntil, int Lockouts) GetStatus(DateTimeOffset now)
    {
        lock (_sync)
        {
            var record = Load();
            if (record == null) return (false, null, 0);
            return (true, record.LockedUntil is { } until && until > now ? until : null, record.TotalLockouts);
        }
    }

    public RemotePinResult Verify(string? pin, DateTimeOffset now)
    {
        lock (_sync)
        {
            var record = Load();
            if (record == null)
                return new(RemotePinStatus.NotConfigured,
                    "Remote changes are not set up. On the child PC, run GameHost.exe --set-remote-pin as administrator.");

            if (record.LockedUntil is { } until && until > now)
                return new(RemotePinStatus.Locked, LockedMessage(until, now), until);

            var ok = false;
            if (!string.IsNullOrEmpty(pin) && pin.Length <= MaxLength)
            {
                var expected = Convert.FromBase64String(record.Hash);
                var actual = Hash(pin, Convert.FromBase64String(record.Salt), record.Iterations);
                ok = CryptographicOperations.FixedTimeEquals(expected, actual);
            }

            if (ok)
            {
                record.Failures = 0;
                record.Locks = 0;
                record.LockedUntil = null;
                TrySave(record);
                return new(RemotePinStatus.Ok, "");
            }

            record.Failures++;
            string message;
            DateTimeOffset? lockedUntil = null;
            if (record.Failures >= MaxFailuresBeforeLock)
            {
                record.Locks++;
                record.TotalLockouts++;
                var duration = TimeSpan.FromTicks(Math.Min(MaxLock.Ticks, FirstLock.Ticks << Math.Min(record.Locks - 1, 7)));
                lockedUntil = now + duration;
                record.LockedUntil = lockedUntil;
                record.Failures = 0;
                message = "Too many wrong PIN attempts. " + LockedMessage(lockedUntil.Value, now);
            }
            else
            {
                message = $"Wrong PIN. {MaxFailuresBeforeLock - record.Failures} attempt(s) left before a lockout.";
            }

            // If the failure cannot be recorded, refuse rather than allow unlimited guessing.
            if (!TrySave(record))
                return new(RemotePinStatus.Locked, "Could not record the attempt. Remote changes are unavailable.", now.AddMinutes(1));
            return new(lockedUntil.HasValue ? RemotePinStatus.Locked : RemotePinStatus.Wrong, message, lockedUntil);
        }
    }

    private static string LockedMessage(DateTimeOffset until, DateTimeOffset now)
    {
        var minutes = (int)Math.Ceiling((until - now).TotalMinutes);
        return $"Remote changes are locked for {minutes} more minute(s). Changes made on the child PC still work.";
    }

    private static byte[] Hash(string pin, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, 32);

    private Record? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var record = JsonSerializer.Deserialize<Record>(File.ReadAllText(_path));
            if (record == null || record.Version != 1 || record.Iterations < 100_000
                || string.IsNullOrEmpty(record.Salt) || string.IsNullOrEmpty(record.Hash))
                return null;
            return record;
        }
        catch
        {
            return null;
        }
    }

    private void Save(Record record)
    {
        lock (_sync)
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            ScreenTimeFiles.WriteAtomic(_path, record);
        }
    }

    private bool TrySave(Record record)
    {
        try { Save(record); return true; }
        catch { return false; }
    }
}
