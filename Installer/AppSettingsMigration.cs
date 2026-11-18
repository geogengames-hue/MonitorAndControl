using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MonitorAndControl.Installer;

/// <summary>What to do with an existing appsettings.json when a new version adds options.</summary>
internal enum AppSettingsAction
{
    /// <summary>Add the new options with their default values; keep every existing value.</summary>
    Merge,
    /// <summary>Leave the file exactly as it is (new options use the program's built-in defaults).</summary>
    Keep,
    /// <summary>Install the new default file; the old one is saved as a backup first.</summary>
    Replace,
}

/// <summary>
/// Keeps a user's appsettings.json across installs and upgrades. Only top-level options that are
/// missing from the existing file count as "new" (matched case-sensitively, like the app's own
/// binding); changed values and the contents of lists such as KnownApps are the user's data.
/// A file is never overwritten without a dated backup.
/// </summary>
internal static class AppSettingsMigration
{
    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Options present in the bundled file but missing from the existing one, with their default values.</summary>
    public static IReadOnlyList<(string Name, string Value)> FindNewOptions(string existingJson, string bundledJson)
    {
        JsonObject existing = ParseObject(existingJson);
        JsonObject bundled = ParseObject(bundledJson);
        var result = new List<(string, string)>();
        foreach (var option in bundled)
        {
            if (!existing.ContainsKey(option.Key))
                result.Add((option.Key, option.Value?.ToJsonString() ?? "null"));
        }
        return result;
    }

    /// <summary>The existing file plus the new options; every existing value is kept. Comments are not preserved.</summary>
    public static string Merge(string existingJson, string bundledJson)
    {
        JsonObject existing = ParseObject(existingJson);
        JsonObject bundled = ParseObject(bundledJson);
        foreach (var option in bundled)
        {
            if (!existing.ContainsKey(option.Key))
                existing[option.Key] = option.Value?.DeepClone();
        }
        return existing.ToJsonString(WriteOptions);
    }

    /// <summary>
    /// Prepares <c>installDir\appsettings.json</c> before the files are copied. The installer's copy step
    /// leaves an existing appsettings.json alone, so this decides what is there when it runs.
    /// </summary>
    /// <param name="installDir">Where the new version is installed.</param>
    /// <param name="previousDir">Folder of an older installation elsewhere (e.g. a manual one), or null.</param>
    /// <param name="bundledPath">The appsettings.json that ships with this version.</param>
    /// <param name="choose">Asked only when the new version has options the existing file lacks.</param>
    /// <param name="log">Progress messages.</param>
    public static void Prepare(string installDir, string? previousDir, string bundledPath,
        Func<IReadOnlyList<(string Name, string Value)>, AppSettingsAction> choose, Action<string> log)
    {
        string target = Path.Combine(installDir, "appsettings.json");
        string? source = null;
        bool imported = false;
        if (File.Exists(target))
        {
            source = target;
        }
        else if (previousDir != null && !SamePath(previousDir, installDir) &&
                 File.Exists(Path.Combine(previousDir, "appsettings.json")))
        {
            source = Path.Combine(previousDir, "appsettings.json");
            imported = true;
        }

        if (source == null || !File.Exists(bundledPath))
        {
            log(InstallerLocalization.NoExistingAppSettings);
            return;
        }

        string existingJson = File.ReadAllText(source);
        IReadOnlyList<(string Name, string Value)> added;
        try
        {
            added = FindNewOptions(existingJson, File.ReadAllText(bundledPath));
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            log(InstallerLocalization.InvalidExistingJson(ex.Message, imported));
            return;
        }

        AppSettingsAction action = added.Count == 0 ? AppSettingsAction.Keep : choose(added);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        Directory.CreateDirectory(installDir);

        switch (action)
        {
            case AppSettingsAction.Merge:
                if (!imported) File.Copy(target, target + ".bak-" + stamp);
                File.WriteAllText(target, Merge(existingJson, File.ReadAllText(bundledPath)));
                log(InstallerLocalization.MergedSettingsLog(imported, added.Count, string.Join(", ", added.Select(a => a.Name))));
                break;

            case AppSettingsAction.Replace:
                if (!imported)
                {
                    File.Copy(target, target + ".bak-" + stamp);
                    File.Delete(target);
                    log(InstallerLocalization.ReplacedSettingsBackupLog(stamp));
                }
                else
                {
                    log(InstallerLocalization.ReplacedSettingsImportedLog);
                }
                break;

            default:
                if (imported)
                {
                    File.Copy(source, target);
                    log(InstallerLocalization.KeptSettingsImportedLog);
                }
                else
                {
                    log(InstallerLocalization.KeptSettingsExistingLog);
                }
                if (added.Count > 0)
                    log(InstallerLocalization.NewOptionsDefaultNotice(string.Join(", ", added.Select(a => a.Name))));
                break;
        }
    }

    private static JsonObject ParseObject(string json) =>
        JsonNode.Parse(json, documentOptions: ParseOptions) as JsonObject
        ?? throw new FormatException("appsettings.json must contain a JSON object.");

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
