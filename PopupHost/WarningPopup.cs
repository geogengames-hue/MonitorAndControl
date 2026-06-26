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
    private readonly Label _detailLabel;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _checkTimer;
    private readonly string _processName;
    private readonly string _appName;
    private readonly string _closingTemplate;
    private readonly string _closingNowText;
    private int _secondsRemaining;

    public WarningPopup(
        string appName,
        int totalSeconds,
        string message,
        string processName,
        string reason = "",
        string detail = "",
        string closingTemplate = "Closing {app} in {seconds} seconds...",
        string closedTemplate = "{app} was closed.",
        string closingNowText = "Closing...")
    {
        _secondsRemaining = totalSeconds;
        _processName = processName;
        _appName = appName;
        _closingTemplate = closingTemplate;
        _closingNowText = closingNowText;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = false;
        Width = 420;
        Height = 170;
        BackColor = Color.FromArgb(30, 30, 46);
        Opacity = 0.95;

        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20)
        };

        _messageLabel = new Label
        {
            Text = string.IsNullOrWhiteSpace(reason) ? message : reason,
            ForeColor = Color.FromArgb(255, 200, 100),
            Font = new Font("Segoe UI", 16, FontStyle.Bold),
            Dock = DockStyle.Top,
            Height = 45,
            TextAlign = ContentAlignment.MiddleCenter
        };

        _countdownLabel = new Label
        {
            Text = totalSeconds > 0
                ? FormatCountdown(totalSeconds)
                : FormatTemplate(closedTemplate, 0),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 20, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter
        };

        _detailLabel = new Label
        {
            Text = string.IsNullOrWhiteSpace(detail) ? message : detail,
            ForeColor = Color.FromArgb(190, 190, 205),
            Font = new Font("Segoe UI", 10, FontStyle.Regular),
            Dock = DockStyle.Bottom,
            Height = 36,
            TextAlign = ContentAlignment.MiddleCenter
        };

        panel.Controls.Add(_detailLabel);
        panel.Controls.Add(_countdownLabel);
        panel.Controls.Add(_messageLabel);
        Controls.Add(panel);

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };

        _checkTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _checkTimer.Tick += (s, e) =>
        {
            if (!string.IsNullOrEmpty(_processName) && !IsProcessRunning(_processName))
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
                _countdownLabel.Text = _closingNowText;
                var closeTimer = new System.Windows.Forms.Timer { Interval = 2000 };
                closeTimer.Tick += (_, __) => { closeTimer.Stop(); Close(); };
                closeTimer.Start();
            }
            else
            {
                _countdownLabel.Text = FormatCountdown(_secondsRemaining);
            }
        };
        if (totalSeconds > 0)
        {
            _timer.Start();
        }
        else
        {
            var closeTimer = new System.Windows.Forms.Timer { Interval = 7000 };
            closeTimer.Tick += (_, __) => { closeTimer.Stop(); Close(); };
            closeTimer.Start();
        }
    }

    private static bool IsProcessRunning(string processName)
    {
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(processName));
        try { return processes.Length > 0; }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    private string FormatCountdown(int seconds) => FormatTemplate(_closingTemplate, seconds);

    private string FormatTemplate(string template, int seconds) =>
        template
            .Replace("{app}", _appName)
            .Replace("{seconds}", seconds.ToString());

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
