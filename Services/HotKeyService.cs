using System.Diagnostics;
using System.Runtime.InteropServices;

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

    public static void HandleHotKey()
    {
        _callback?.Invoke();
    }

    /// <summary>
    /// Parse modifier string like "Control+Alt" into flags and key char.
    /// </summary>
    public static (uint Modifiers, uint Key) ParseHotKey(string modifiers, string key)
    {
        uint modFlags = NativeMethods.MOD_NOREPEAT;

        foreach (var part in modifiers.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            modFlags |= part.Trim().ToLowerInvariant() switch
            {
                "control" or "ctrl" => NativeMethods.MOD_CONTROL,
                "alt" => NativeMethods.MOD_ALT,
                "shift" => NativeMethods.MOD_SHIFT,
                "win" or "windows" => NativeMethods.MOD_WIN,
                _ => 0
            };
        }

        uint vk = (uint)(key.ToUpperInvariant()[0]);
        return (modFlags, vk);
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
