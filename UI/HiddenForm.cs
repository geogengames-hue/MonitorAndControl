using System.Runtime.InteropServices;
using MonitorAndControl.Services;

namespace MonitorAndControl.UI;

public class HiddenForm : Form
{
    private readonly int _hotKeyId;
    private readonly Action _onHotKey;
    private readonly uint _hotKeyMods;
    private readonly uint _hotKeyVk;
    private bool _shown;

    public HiddenForm(int hotKeyId, uint mods, uint vk, Action onHotKey)
    {
        _hotKeyId = hotKeyId;
        _hotKeyMods = mods;
        _hotKeyVk = vk;
        _onHotKey = onHotKey;

        WindowState = FormWindowState.Minimized;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;

        _ = Handle;
        NativeMethods.ShowWindow(Handle, NativeMethods.SW_HIDE);
        HotKeyService.Register(Handle, _hotKeyId, _hotKeyMods, _hotKeyVk, _onHotKey);
    }

    protected override void SetVisibleCore(bool value)
    {
        if (!_shown)
        {
            _shown = true;
            base.SetVisibleCore(false);
            return;
        }
        base.SetVisibleCore(value);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_HOTKEY && (int)m.WParam == _hotKeyId)
        {
            _onHotKey();
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        NativeMethods.UnregisterHotKey(Handle, _hotKeyId);
        base.OnFormClosing(e);
    }
}
