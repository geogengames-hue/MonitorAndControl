using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MonitorAndControl.Services;

public static class HotKeyService
{
    private static int _hotKeyId;
    private static IntPtr _hwnd;
    private static Action? _callback;

    public static bool Register(IntPtr windowHandle, int id, uint modifiers, uint key, Action callback)
    {
        _hwnd = windowHandle;
        _hotKeyId = id;
        _callback = callback;

        var result = NativeMethods.RegisterHotKey(windowHandle, id, modifiers, key);
        if (!result)
            Debug.WriteLine($"Failed to register hotkey. Error: {Marshal.GetLastPInvokeError()}");
        return result;
    }

    public static void Unregister()
    {
        if (_hwnd != IntPtr.Zero)
            NativeMethods.UnregisterHotKey(_hwnd, _hotKeyId);
    }

    public static void Unregister(IntPtr windowHandle, int id)
    {
        if (windowHandle != IntPtr.Zero)
            NativeMethods.UnregisterHotKey(windowHandle, id);
    }

    public static void HandleHotKey()
    {
        _callback?.Invoke();
    }

    /// <summary>
    /// Parse modifier string like "Control+Alt" into flags and key char.
    /// </summary>
    public static (uint Modifiers, uint Key) ParseHotKey(string modifiers, string key)
    {
        if (!TryParseHotKey(modifiers, key, out var modFlags, out var vk, out _, out _, out var error))
            throw new ArgumentException(error);
        return (modFlags, vk);
    }

    public static bool TryParseHotKey(string modifiers, string key, out uint modFlags, out uint vk,
        out string normalizedModifiers, out string normalizedKey, out string error)
    {
        modFlags = NativeMethods.MOD_NOREPEAT;
        vk = 0;
        normalizedModifiers = "";
        normalizedKey = "";
        error = "";

        var parts = modifiers.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var normalizedParts = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var part in parts)
        {
            var normalized = part.Trim().ToLowerInvariant() switch
            {
                "control" or "ctrl" => "Control",
                "alt" => "Alt",
                "shift" => "Shift",
                "win" or "windows" => "Win",
                _ => ""
            };

            if (normalized.Length == 0)
            {
                error = $"Unsupported hotkey modifier: {part}.";
                return false;
            }

            if (!seen.Add(normalized))
                continue;

            normalizedParts.Add(normalized);
            modFlags |= normalized switch
            {
                "Control" => NativeMethods.MOD_CONTROL,
                "Alt" => NativeMethods.MOD_ALT,
                "Shift" => NativeMethods.MOD_SHIFT,
                "Win" => NativeMethods.MOD_WIN,
                _ => 0
            };
        }

        if (normalizedParts.Count == 0)
        {
            error = "Choose at least one hotkey modifier.";
            return false;
        }

        if (!TryParseVirtualKey(key, out vk, out normalizedKey))
        {
            error = "Unsupported hotkey key.";
            return false;
        }

        normalizedModifiers = string.Join("+", normalizedParts);
        return true;
    }

    private static bool TryParseVirtualKey(string key, out uint vk, out string normalizedKey)
    {
        vk = 0;
        normalizedKey = "";
        var clean = (key ?? "").Trim();
        if (clean.Length == 1 && char.IsLetterOrDigit(clean[0]))
        {
            normalizedKey = clean.ToUpperInvariant();
            vk = normalizedKey[0];
            return true;
        }

        if (clean.Length is 2 or 3 &&
            clean.StartsWith("F", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(clean[1..], out var fKey) &&
            fKey is >= 1 and <= 24)
        {
            normalizedKey = $"F{fKey}";
            vk = (uint)((int)Keys.F1 + fKey - 1);
            return true;
        }

        var namedKeys = new Dictionary<string, Keys>(StringComparer.OrdinalIgnoreCase)
        {
            ["Insert"] = Keys.Insert,
            ["Delete"] = Keys.Delete,
            ["Home"] = Keys.Home,
            ["End"] = Keys.End,
            ["PageUp"] = Keys.PageUp,
            ["PageDown"] = Keys.PageDown,
            ["Up"] = Keys.Up,
            ["Down"] = Keys.Down,
            ["Left"] = Keys.Left,
            ["Right"] = Keys.Right,
            ["Space"] = Keys.Space
        };

        if (!namedKeys.TryGetValue(clean.Replace(" ", ""), out var parsed))
            return false;

        normalizedKey = namedKeys.Keys.First(k => k.Equals(clean.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
        vk = (uint)parsed;
        return true;
    }

    public static void OpenDashboard(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }
}
