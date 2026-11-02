using System.Text.RegularExpressions;

namespace MonitorAndControl.Services;

/// <summary>
/// Turns what a browser's address bar shows into a plain site name. Only the host is ever kept:
/// never the path, query, page title or anything typed into the address bar.
/// </summary>
public static class SiteName
{
    public const string PrivateWindow = "(private window)";
    public const string UnknownSite = "(unknown site)";

    private static readonly Regex Scheme = new(@"^[a-z][a-z0-9+.\-]*://", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HostChars = new(@"^[a-z0-9.\-]+$", RegexOptions.Compiled);

    /// <summary>True for a normal website; false for blank text, search text being typed and internal browser pages.</summary>
    public static bool TryGetHost(string? raw, out string host)
    {
        host = "";
        var text = (raw ?? "").Trim();
        if (text.Length == 0 || text.Length > 2048 || text.Any(char.IsWhiteSpace)) return false;

        var scheme = Scheme.Match(text);
        if (scheme.Success)
        {
            var name = scheme.Value[..^3].ToLowerInvariant();
            if (name is not ("http" or "https")) return false; // chrome://, edge://, file://, about: ...
            text = text[scheme.Length..];
        }
        else if (text.Contains(':') && !text.Contains('.')) return false; // about:blank and the like

        var end = text.IndexOfAny(new[] { '/', '?', '#' });
        if (end >= 0) text = text[..end];
        var at = text.LastIndexOf('@');
        if (at >= 0) text = text[(at + 1)..];
        var colon = text.LastIndexOf(':');
        if (colon >= 0 && text[(colon + 1)..].All(char.IsDigit)) text = text[..colon];

        text = text.Trim('.').ToLowerInvariant();
        if (text.StartsWith("www.")) text = text[4..];
        if (text.Length == 0 || text.Length > 253 || !HostChars.IsMatch(text)) return false;
        if (!text.Contains('.') && text != "localhost") return false;
        host = text;
        return true;
    }

    public static bool IsPrivateTitle(string? title)
    {
        if (string.IsNullOrEmpty(title)) return false;
        return title.Contains("InPrivate", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Incognito", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Private Browsing", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Private window", StringComparison.OrdinalIgnoreCase);
    }
}
