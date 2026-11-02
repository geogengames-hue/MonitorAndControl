using MonitorAndControl.Data;
using MonitorAndControl.Services;

namespace MonitorAndControl.UI;

/// <summary>
/// An optional tray icon for the child's own session. Hovering shows the time left; clicking shows the
/// details. It is off unless the parent turns on "show time left" (the rest of DeviceMon stays invisible).
/// </summary>
public sealed class TimeLeftTray : IDisposable
{
    private readonly UsageDatabase _db;
    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.Timer _timer;
    private TimeLeftView? _last;
    private bool _busy;

    public TimeLeftTray(UsageDatabase db)
    {
        _db = db;
        _icon = new NotifyIcon { Icon = SystemIcons.Information, Text = "Time left", Visible = false };
        _icon.MouseClick += (_, _) => ShowDetails();
        _timer = new System.Windows.Forms.Timer { Interval = 30000 };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _ = FirstRefreshAsync();
    }

    private async Task FirstRefreshAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (await _db.GetSettingAsync("ShowTimeLeft", "false") != "true")
            {
                _icon.Visible = false;
                return;
            }
            _last = await TimeLeftReport.CollectAsync(_db);
            _icon.Text = _last.Tooltip;
            _icon.Visible = true;
        }
        catch (Exception ex) { Logger.Instance.Warn("Time-left icon could not refresh: " + ex.Message); }
        finally { _busy = false; }
    }

    private void ShowDetails()
    {
        var text = string.Join("\n", (_last?.Lines ?? new List<string> { "Loading..." }));
        if (text.Length > 250) text = text[..247] + "...";
        _icon.ShowBalloonTip(10000, "Time left", text, ToolTipIcon.None);
    }

    public void Dispose()
    {
        _timer.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
    }
}
