using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PopupHost;

internal static class NativeMethods
{
    [DllImport("user32.dll")]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    internal struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    internal static readonly IntPtr HWND_TOPMOST = new(-1);
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_SHOWWINDOW = 0x0040;
    internal const int SW_MINIMIZE = 6;
}

public class WarningPopup : Form
{
    private readonly Label _messageLabel;
    private readonly Label _countdownLabel;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _checkTimer;
    private readonly string _processName;
    private int _secondsRemaining;

    public WarningPopup(string appName, int totalSeconds, string message, string processName)
    {
        _secondsRemaining = totalSeconds;
        _processName = processName;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = false;
        Width = 420;
        Height = 130;
        BackColor = Color.FromArgb(30, 30, 46);
        Opacity = 0.95;

        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20)
        };

        _messageLabel = new Label
        {
            Text = message,
            ForeColor = Color.FromArgb(255, 200, 100),
            Font = new Font("Segoe UI", 16, FontStyle.Bold),
            Dock = DockStyle.Top,
            Height = 45,
            TextAlign = ContentAlignment.MiddleCenter
        };

        _countdownLabel = new Label
        {
            Text = $"Closing {appName} in {totalSeconds} seconds...",
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 20, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter
        };

        panel.Controls.Add(_countdownLabel);
        panel.Controls.Add(_messageLabel);
        Controls.Add(panel);

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };

        _checkTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _checkTimer.Tick += (s, e) =>
        {
            if (!string.IsNullOrEmpty(_processName) &&
                Process.GetProcessesByName(Path.GetFileNameWithoutExtension(_processName)).Length == 0)
            {
                _checkTimer.Stop();
                _timer.Stop();
                Close();
            }
        };
        _checkTimer.Start();
        _timer.Tick += (s, e) =>
        {
            _secondsRemaining--;
            if (_secondsRemaining <= 0)
            {
                _timer.Stop();
                _countdownLabel.Text = "Closing...";
                var closeTimer = new System.Windows.Forms.Timer { Interval = 2000 };
                closeTimer.Tick += (_, __) => { closeTimer.Stop(); Close(); };
                closeTimer.Start();
            }
            else
            {
                _countdownLabel.Text = $"Closing {appName} in {_secondsRemaining} seconds...";
            }
        };
        _timer.Start();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Minimize the foreground window only if it's fullscreen
        var fg = NativeMethods.GetForegroundWindow();
        if (fg != IntPtr.Zero && fg != Handle)
        {
            if (NativeMethods.GetWindowRect(fg, out var rect))
            {
                var screenW = Screen.PrimaryScreen?.Bounds.Width ?? 0;
                var screenH = Screen.PrimaryScreen?.Bounds.Height ?? 0;
                var w = rect.Right - rect.Left;
                var h = rect.Bottom - rect.Top;
                if (w >= screenW && h >= screenH)
                    NativeMethods.ShowWindow(fg, NativeMethods.SW_MINIMIZE);
            }
        }

        var screen = Screen.PrimaryScreen?.WorkingArea;
        if (screen.HasValue)
        {
            Left = (screen.Value.Width - Width) / 2;
            Top = screen.Value.Height - Height - 60;
        }
        TopMost = true;
        NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_SHOWWINDOW);
        BringToFront();
        Activate();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _checkTimer.Stop();
        _timer.Stop();
        base.OnFormClosing(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
            Close();
        base.OnKeyDown(e);
    }

    protected override void OnClick(EventArgs e)
    {
        Close();
        base.OnClick(e);
    }
}
