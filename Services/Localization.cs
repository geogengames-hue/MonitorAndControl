using System.Globalization;
using System.Resources;

namespace MonitorAndControl.Services;

public static class Localization
{
    private static readonly ResourceManager Resources =
        new("MonitorAndControl.Resources.Strings", typeof(Localization).Assembly);

    public static string NormalizeLanguage(string? language)
    {
        var value = (language ?? "en").Trim().ToLowerInvariant();
        return value is "de" or "es" or "ru" or "fr" ? value : "en";
    }

    public static string Text(string key, string? language, params object[] args)
    {
        var normalized = NormalizeLanguage(language);
        var culture = normalized == "en"
            ? CultureInfo.InvariantCulture
            : CultureInfo.GetCultureInfo(normalized);
        var value = Resources.GetString(key, culture) ?? Resources.GetString(key, CultureInfo.InvariantCulture) ?? key;
        return args.Length == 0 ? value : string.Format(culture, value, args);
    }
}
