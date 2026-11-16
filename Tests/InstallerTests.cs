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

    public static Task InstallerDetectsAndNormalizesLanguages()
    {
        // Normalization
        Check(InstallerLocalization.NormalizeLanguage("es") == "es", "es must normalize to es");
        Check(InstallerLocalization.NormalizeLanguage("es-ES") == "es", "es-ES must normalize to es");
        Check(InstallerLocalization.NormalizeLanguage("es-MX") == "es", "es-MX must normalize to es");
        Check(InstallerLocalization.NormalizeLanguage("de") == "de", "de must normalize to de");
        Check(InstallerLocalization.NormalizeLanguage("de-DE") == "de", "de-DE must normalize to de");
        Check(InstallerLocalization.NormalizeLanguage("fr") == "fr", "fr must normalize to fr");
        Check(InstallerLocalization.NormalizeLanguage("fr-FR") == "fr", "fr-FR must normalize to fr");
        Check(InstallerLocalization.NormalizeLanguage("ru") == "ru", "ru must normalize to ru");
        Check(InstallerLocalization.NormalizeLanguage("ru-RU") == "ru", "ru-RU must normalize to ru");
        Check(InstallerLocalization.NormalizeLanguage("en") == "en", "en must normalize to en");
        Check(InstallerLocalization.NormalizeLanguage("en-US") == "en", "en-US must normalize to en");
        Check(InstallerLocalization.NormalizeLanguage("ja-JP") == "en", "Unsupported language ja-JP must fallback to en");
        Check(InstallerLocalization.NormalizeLanguage("zh-CN") == "en", "Unsupported language zh-CN must fallback to en");
        Check(InstallerLocalization.NormalizeLanguage("it") == "en", "Unsupported language it must fallback to en");
        Check(InstallerLocalization.NormalizeLanguage("") == "en", "Empty must fallback to en");
        Check(InstallerLocalization.NormalizeLanguage(null) == "en", "Null must fallback to en");

        // Win32 LANGID mapping
        Check(InstallerLocalization.LanguageFromLangId(0x0c0a) == "es", "0x0c0a (Spanish) maps to es");
        Check(InstallerLocalization.LanguageFromLangId(0x040a) == "es", "0x040a (Spanish) maps to es");
        Check(InstallerLocalization.LanguageFromLangId(0x0407) == "de", "0x0407 (German) maps to de");
        Check(InstallerLocalization.LanguageFromLangId(0x040c) == "fr", "0x040c (French) maps to fr");
        Check(InstallerLocalization.LanguageFromLangId(0x0419) == "ru", "0x0419 (Russian) maps to ru");
        Check(InstallerLocalization.LanguageFromLangId(0x0409) == "en", "0x0409 (English US) maps to en");
        Check(InstallerLocalization.LanguageFromLangId(0x0411) == "en", "0x0411 (Japanese) fallbacks to en");

        // Detection returns a supported language
        string detected = InstallerLocalization.DetectLanguage();
        Check(new[] { "en", "es", "de", "fr", "ru" }.Contains(detected), "DetectLanguage must return a supported language");

        return Task.CompletedTask;
    }

    public static Task InstallerMessagesArePopulatedForAllSupportedLanguages()
    {
        string[] supported = { "en", "es", "de", "fr", "ru" };
        foreach (var lang in supported)
        {
            InstallerLocalization.Language = lang;
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.ButtonClose), $"ButtonClose for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.ButtonContinue), $"ButtonContinue for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.ElevationRequiredMessage), $"ElevationRequiredMessage for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.Starting), $"Starting for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.ExtractingFiles), $"ExtractingFiles for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.InstallingMonitorAndControl), $"InstallingMonitorAndControl for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.InstalledSuccess("1.0")), $"InstalledSuccess for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.SetupFailed), $"SetupFailed for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.VerifyingRuntime(".NET")), $"VerifyingRuntime for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.InstallingRuntimeProgress(".NET", "0:05")), $"InstallingRuntimeProgress for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.DownloadingRuntime(".NET")), $"DownloadingRuntime for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.DownloadingRuntimeProgress(".NET", 10.0, 50.0)), $"DownloadingRuntimeProgress for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.RunningAsAdmin("Admin")), $"RunningAsAdmin for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.RuntimeNotInstalled(".NET")), $"RuntimeNotInstalled for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.InstallingRuntimePrompt(".NET")), $"InstallingRuntimePrompt for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.RuntimeInstalled(".NET")), $"RuntimeInstalled for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.VersionToInstall("1.0")), $"VersionToInstall for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.VersionOnDiskNone), $"VersionOnDiskNone for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.VersionOnDiskFound("1.0", "C:\\Dir")), $"VersionOnDiskFound for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.SameVersionReinstall), $"SameVersionReinstall for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.UpgradingVersion("1.0", "2.0")), $"UpgradingVersion for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.DowngradingVersion("2.0", "1.0")), $"DowngradingVersion for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.PreviousInstallReplaced("C:\\Old")), $"PreviousInstallReplaced for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.InstalledVersionLog("1.0")), $"InstalledVersionLog for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.SetupFinished), $"SetupFinished for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.InvalidAppSettings("Bad")), $"InvalidAppSettings for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.InstallScriptFailed(1)), $"InstallScriptFailed for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.DownloadNotSigned), $"DownloadNotSigned for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.CouldNotStartRuntimeInstaller), $"CouldNotStartRuntimeInstaller for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.RuntimeInstallerTimeout), $"RuntimeInstallerTimeout for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.RuntimeInstallerExitCode(1)), $"RuntimeInstallerExitCode for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.RuntimeInstallFailed(".NET", "err", "C:\\log.txt")), $"RuntimeInstallFailed for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.NoPayload), $"NoPayload for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.InstallScriptMissing), $"InstallScriptMissing for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.CouldNotStartPowerShell), $"CouldNotStartPowerShell for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.SettingsDialogTitle), $"SettingsDialogTitle for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.SettingsIntro), $"SettingsIntro for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.SettingsAsk), $"SettingsAsk for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.SettingsMergeOption), $"SettingsMergeOption for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.SettingsKeepOption), $"SettingsKeepOption for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.SettingsReplaceOption), $"SettingsReplaceOption for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.NoExistingAppSettings), $"NoExistingAppSettings for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.InvalidExistingJson("err", false)), $"InvalidExistingJson for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.MergedSettingsLog(false, 1, "Opt")), $"MergedSettingsLog for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.ReplacedSettingsBackupLog("20261116")), $"ReplacedSettingsBackupLog for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.ReplacedSettingsImportedLog), $"ReplacedSettingsImportedLog for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.KeptSettingsImportedLog), $"KeptSettingsImportedLog for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.KeptSettingsExistingLog), $"KeptSettingsExistingLog for {lang}");
            Check(!string.IsNullOrWhiteSpace(InstallerLocalization.NewOptionsDefaultNotice("Opt")), $"NewOptionsDefaultNotice for {lang}");
        }
        return Task.CompletedTask;
    }
}
