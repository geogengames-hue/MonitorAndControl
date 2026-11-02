using System.Windows.Automation;

namespace MonitorAndControl.Services;

/// <summary>
/// Reads the address bar of the foreground browser window through Windows UI Automation, so no
/// browser extension is needed. Chrome, Edge, Brave, Opera and Vivaldi share the same address-bar
/// control; Firefox is read on a best-effort basis.
/// </summary>
internal sealed class BrowserAddressReader
{
    private static readonly string[] Chromium = { "chrome", "msedge", "brave", "opera", "vivaldi" };
    private readonly Dictionary<IntPtr, AutomationElement> _cache = new();

    public static bool IsBrowser(string? processName)
    {
        var name = Normalize(processName);
        return name == "firefox" || Chromium.Contains(name);
    }

    private static string Normalize(string? processName) =>
        Path.GetFileNameWithoutExtension(processName ?? "").ToLowerInvariant();

    /// <summary>Returns the address-bar text, or null when it cannot be read. Never throws.</summary>
    public string? Read(IntPtr window, string processName)
    {
        try
        {
            if (_cache.TryGetValue(window, out var cached))
            {
                var value = ValueOf(cached);
                if (value != null) return value;
                _cache.Remove(window);
            }
            var root = AutomationElement.FromHandle(window);
            var bar = Normalize(processName) == "firefox"
                ? root.FindFirst(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "urlbar-input")))
                : root.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ClassNameProperty, "OmniboxViewViews"));
            if (bar == null) return null;
            var text = ValueOf(bar);
            if (text != null)
            {
                if (_cache.Count > 20) _cache.Clear();
                _cache[window] = bar;
            }
            return text;
        }
        catch { return null; }
    }

    private static string? ValueOf(AutomationElement element)
    {
        try
        {
            return element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)
                ? ((ValuePattern)pattern).Current.Value
                : null;
        }
        catch { return null; }
    }
}
