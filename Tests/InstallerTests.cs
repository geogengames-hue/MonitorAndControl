using System.Text.Json;
using MonitorAndControl.Installer;
using MonitorAndControl.Models;

internal static class InstallerTests
{
    private const string Bundled = """
        { "DashboardPort": 5000, "EnableRemoteDashboard": true, "NewOption": 42, "NewText": "a+b & c",
          "KnownApps": { "a.exe": "A", "b.exe": "B" } }
        """;

    private const string UserFile = """
        { "DashboardPort": 8080, "EnableRemoteDashboard": false, "KnownApps": { "mine.exe": "Mine" } }
        """;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mc-installer-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static Task NewOptionsAreFoundAndMergedWithoutLosingValues()
    {
        var added = AppSettingsMigration.FindNewOptions(UserFile, Bundled);
        Check(added.Select(a => a.Name).OrderBy(n => n).SequenceEqual(new[] { "NewOption", "NewText" }),
            "Only keys missing from the user's file are new (changed values and KnownApps entries are not).");

        var merged = JsonSerializer.Deserialize<AppConfig>(AppSettingsMigration.Merge(UserFile, Bundled))!;
        Check(merged.DashboardPort == 8080 && !merged.EnableRemoteDashboard, "Existing values must be kept.");
        Check(merged.KnownApps.Count == 1 && merged.KnownApps.ContainsKey("mine.exe"), "The user's own list must not gain default entries.");
        Check(AppSettingsMigration.Merge(UserFile, Bundled).Contains("a+b & c"), "Merged text must not be escaped.");
        Check(AppSettingsMigration.FindNewOptions(AppSettingsMigration.Merge(UserFile, Bundled), Bundled).Count == 0,
            "After a merge nothing is new any more.");
        return Task.CompletedTask;
    }

    public static Task AppSettingsActionsBackUpAndNeverAskWhenNothingIsNew()
    {
        string root = NewTempDir();
        try
        {
            string bundled = Path.Combine(root, "bundled.json");
            File.WriteAllText(bundled, Bundled);
            var log = new List<string>();

            // Merge: user value kept, new option added, dated backup written.
            string dir1 = Path.Combine(root, "merge");
            Directory.CreateDirectory(dir1);
            File.WriteAllText(Path.Combine(dir1, "appsettings.json"), UserFile);
            AppSettingsMigration.Prepare(dir1, null, bundled, _ => AppSettingsAction.Merge, log.Add);
            Check(File.ReadAllText(Path.Combine(dir1, "appsettings.json")).Contains("NewOption"), "Merge must add the new option.");
            Check(Directory.GetFiles(dir1, "appsettings.json.bak-*").Length == 1, "Merge must leave a dated backup.");

            // Keep: file untouched, no backup.
            string dir2 = Path.Combine(root, "keep");
            Directory.CreateDirectory(dir2);
            File.WriteAllText(Path.Combine(dir2, "appsettings.json"), UserFile);
            AppSettingsMigration.Prepare(dir2, null, bundled, _ => AppSettingsAction.Keep, log.Add);
            Check(File.ReadAllText(Path.Combine(dir2, "appsettings.json")) == UserFile, "Keep must not touch the file.");
            Check(Directory.GetFiles(dir2, "*.bak-*").Length == 0, "Keep must not create a backup.");

            // Replace: file removed (so the installer copies the default), backup holds the user's file.
            string dir3 = Path.Combine(root, "replace");
            Directory.CreateDirectory(dir3);
            File.WriteAllText(Path.Combine(dir3, "appsettings.json"), UserFile);
            AppSettingsMigration.Prepare(dir3, null, bundled, _ => AppSettingsAction.Replace, log.Add);
            Check(!File.Exists(Path.Combine(dir3, "appsettings.json")), "Replace must clear the way for the default file.");
            Check(File.ReadAllText(Directory.GetFiles(dir3, "appsettings.json.bak-*").Single()) == UserFile, "The backup must hold the user's file.");

            // Nothing new: never asks.
            string dir4 = Path.Combine(root, "same");
            Directory.CreateDirectory(dir4);
            File.WriteAllText(Path.Combine(dir4, "appsettings.json"), Bundled);
            AppSettingsMigration.Prepare(dir4, null, bundled, _ => throw new InvalidOperationException("must not ask"), log.Add);
            Check(File.ReadAllText(Path.Combine(dir4, "appsettings.json")) == Bundled, "An up-to-date file must stay as it is.");

            // Invalid JSON: left alone, no question, no exception.
            string dir5 = Path.Combine(root, "broken");
            Directory.CreateDirectory(dir5);
            File.WriteAllText(Path.Combine(dir5, "appsettings.json"), "{ not json");
            AppSettingsMigration.Prepare(dir5, null, bundled, _ => throw new InvalidOperationException("must not ask"), log.Add);
            Check(File.ReadAllText(Path.Combine(dir5, "appsettings.json")) == "{ not json", "An unreadable file must be left untouched.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
        return Task.CompletedTask;
    }

    public static Task SettingsOfAnOlderInstallAreCarriedOver()
    {
        string root = NewTempDir();
        try
        {
            string bundled = Path.Combine(root, "bundled.json");
            File.WriteAllText(bundled, Bundled);
            string oldDir = Path.Combine(root, "old");
            string newDir = Path.Combine(root, "new");
            Directory.CreateDirectory(oldDir);
            File.WriteAllText(Path.Combine(oldDir, "appsettings.json"), UserFile);

            AppSettingsMigration.Prepare(newDir, oldDir, bundled, _ => AppSettingsAction.Merge, _ => { });
            var imported = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Path.Combine(newDir, "appsettings.json")))!;
            Check(imported.DashboardPort == 8080, "The old installation's value must be carried over.");
            Check(File.ReadAllText(Path.Combine(oldDir, "appsettings.json")) == UserFile, "The old installation's file must not be modified.");
            Check(Directory.GetFiles(newDir, "*.bak-*").Length == 0, "Nothing to back up when importing.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
        return Task.CompletedTask;
    }
}
