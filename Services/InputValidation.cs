namespace MonitorAndControl.Services;

public static class InputValidation
{
    public static string Clean(string? value) => (value ?? "").Trim();

    public static bool IsValidAppName(string value) =>
        value.Length is > 0 and <= 120 && !value.Any(char.IsControl);

    public static bool IsValidProcessName(string value) =>
        value.Length is > 4 and <= 260 &&
        value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(value).Equals(value, StringComparison.OrdinalIgnoreCase) &&
        !value.Any(char.IsControl);

    public static bool IsValidLimitMinutes(int minutes) => minutes is >= 1 and <= 1440;

    public static bool IsValidBonusMinutes(int minutes) => minutes is >= 1 and <= 240;

    public static bool IsValidKillDelaySeconds(int seconds) => seconds is >= 5 and <= 300;

    public static bool IsValidHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
