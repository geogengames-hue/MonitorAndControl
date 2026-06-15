namespace PopupHost;

public class WarningPopup : Form
{
    private readonly Label _messageLabel;
    private readonly Label _countdownLabel;
    private readonly System.Windows.Forms.Timer _timer;
    private int _secondsRemaining;

    public WarningPopup(string appName, int totalSeconds, string message)
    {
        _secondsRemaining = totalSeconds;

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
        var screen = Screen.PrimaryScreen?.WorkingArea;
        if (screen.HasValue)
        {
            Left = (screen.Value.Width - Width) / 2;
            Top = screen.Value.Height - Height - 60;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            _timer.Stop();
            Close();
        }
        base.OnKeyDown(e);
    }

    protected override void OnClick(EventArgs e)
    {
        _timer.Stop();
        Close();
        base.OnClick(e);
    }
}
