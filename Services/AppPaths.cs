namespace MonitorAndControl.Services;

/// <summary>
/// Central resolver for the application data directory and the tamper-sensitive
/// files inside it (the usage database and the event log).
///
/// These files used to live under %LocalAppData%\SystemHelper, which sits inside
/// the (standard) child user's own profile - so the child owned them and could
/// delete monitor.db / events.log after closing DeviceMon. They now live under
/// %ProgramData%\SystemHelper, the same directory the SYSTEM GameHost watchdog
/// manages, so the watchdog can lock them down against deletion.
/// </summary>
public static class AppPaths
{
    private const string FolderName = "SystemHelper";

    /// <summary>Resolved data directory (ProgramData, with a LocalAppData fallback).</summary>
    public static string DataDir { get; }

    static AppPaths()
    {
        var primary = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            FolderName);
        try
        {
            Directory.CreateDirectory(primary);
            DataDir = primary;
        }
        catch
        {
            // Extremely rare - fall back to the legacy per-user location so the
            // app still starts even if ProgramData is not writable.
            var fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                FolderName);
            try { Directory.CreateDirectory(fallback); } catch { }
            DataDir = fallback;
        }

        TryMigrateLegacy();
    }

    public static string DatabasePath => Path.Combine(DataDir, "monitor.db");
    public static string LogPath => Path.Combine(DataDir, "events.log");

    /// <summary>
    /// Moves an existing database / log from the old %LocalAppData%\SystemHelper
    /// location into the new protected directory the first time we run there.
    /// </summary>
    private static void TryMigrateLegacy()
    {
        try
        {
            var legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                FolderName);

            if (string.Equals(Path.GetFullPath(legacy), Path.GetFullPath(DataDir), StringComparison.OrdinalIgnoreCase))
                return; // Already running from the legacy location (fallback case).

            MoveIfNeeded(Path.Combine(legacy, "monitor.db"), DatabasePath);
            MoveIfNeeded(Path.Combine(legacy, "monitor.db-wal"), DatabasePath + "-wal");
            MoveIfNeeded(Path.Combine(legacy, "monitor.db-shm"), DatabasePath + "-shm");
            MoveIfNeeded(Path.Combine(legacy, "events.log"), LogPath);
        }
        catch
        {
            // Migration is best-effort; a fresh database will be created if it fails.
        }
    }

    private static void MoveIfNeeded(string src, string dst)
    {
        try
        {
            if (!File.Exists(src) || File.Exists(dst)) return;
            File.Move(src, dst);
        }
        catch
        {
            try { if (File.Exists(src) && !File.Exists(dst)) File.Copy(src, dst); }
            catch { }
        }
    }
}
