using System.Net.NetworkInformation;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace MonitorAndControl.Installer;

/// <summary>
/// Helper for installer port checks and remote PIN creation.
/// </summary>
internal static class RemotePinSetup
{
    public const int MinLength = 6;
    public const int MaxLength = 64;
    private const int DefaultIterations = 210_000;

    private sealed class Record
    {
        public int Version { get; set; } = 1;
        public string Salt { get; set; } = "";
        public string Hash { get; set; } = "";
        public int Iterations { get; set; } = DefaultIterations;
        public int Failures { get; set; }
        public int Locks { get; set; }
        public int TotalLockouts { get; set; }
        public DateTimeOffset? LockedUntil { get; set; }
    }

    /// <summary>
    /// Checks if a TCP port is in use by an active listener.
    /// </summary>
    public static bool IsPortInUse(int port)
    {
        try
        {
            var properties = IPGlobalProperties.GetIPGlobalProperties();
            var listeners = properties.GetActiveTcpListeners();
            return listeners.Any(endpoint => endpoint.Port == port);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks whether a PIN meets length and complexity requirements.
    /// </summary>
    public static bool IsAcceptable(string? pin, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(pin) || pin.Length < MinLength || pin.Length > MaxLength)
            error = InstallerLocalization.RemotePinTooShortOrWeak;
        else if (pin.Distinct().Count() < 3)
            error = InstallerLocalization.RemotePinTooShortOrWeak;
        return error.Length == 0;
    }

    /// <summary>
    /// Checks if a valid remote PIN is already configured in the protected data folder.
    /// </summary>
    public static bool IsConfigured(string dataDirectory)
    {
        try
        {
            string pinFile = Path.Combine(dataDirectory, "Protected", "remote-pin.json");
            if (!File.Exists(pinFile)) return false;
            string json = File.ReadAllText(pinFile);
            var record = JsonSerializer.Deserialize<Record>(json);
            return record != null && record.Version == 1 && !string.IsNullOrEmpty(record.Salt) && !string.IsNullOrEmpty(record.Hash);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sets the remote PIN in the protected data directory and locks permissions to SYSTEM and Administrators.
    /// </summary>
    public static void SetPin(string dataDirectory, string pin)
    {
        if (!IsAcceptable(pin, out var error))
            throw new ArgumentException(error);

        string protectedDir = Path.Combine(dataDirectory, "Protected");
        Directory.CreateDirectory(protectedDir);

        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, DefaultIterations, HashAlgorithmName.SHA256, 32);

        var record = new Record
        {
            Salt = Convert.ToBase64String(salt),
            Hash = Convert.ToBase64String(hash),
        };

        string pinFile = Path.Combine(protectedDir, "remote-pin.json");
        string tempFile = pinFile + ".tmp";
        File.WriteAllText(tempFile, JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
        if (File.Exists(pinFile))
        {
            File.Replace(tempFile, pinFile, null);
        }
        else
        {
            File.Move(tempFile, pinFile);
        }

        ProtectDirectory(protectedDir);
    }

    /// <summary>
    /// Restricts directory access to SYSTEM and Administrators only when running elevated.
    /// </summary>
    public static void ProtectDirectory(string directory)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            if (!principal.IsInRole(WindowsBuiltInRole.Administrator)) return;

            var dirInfo = new DirectoryInfo(directory);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var adminsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

            security.AddAccessRule(new FileSystemAccessRule(systemSid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(adminsSid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));

            dirInfo.SetAccessControl(security);
        }
        catch
        {
            // Best effort; install.ps1 also enforces with icacls
        }
    }
}
