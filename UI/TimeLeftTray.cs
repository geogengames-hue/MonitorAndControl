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
    private string _language = "en";
    private bool _busy;

    public TimeLeftTray(UsageDatabase db)
    {
        _db = db;
        _icon = new NotifyIcon { Icon = SystemIcons.Information, Text = Localization.Text("TimeLeft", "en"), Visible = false };
        _icon.MouseClick += async (_, _) => await ShowDetailsAsync();
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
            _language = await _db.GetSettingAsync("UiLanguage", "en");
            _last = await TimeLeftReport.CollectAsync(_db, _language);
            _icon.Text = _last.Tooltip;
            _icon.Visible = true;
        }
        catch (Exception ex) { Logger.Instance.Warn("Time-left icon could not refresh: " + ex.Message); }
        finally { _busy = false; }
    }

    private async Task ShowDetailsAsync()
    {
        try { _language = await _db.GetSettingAsync("UiLanguage", "en"); } catch { }
        var loading = Localization.Text("Loading", _language);
        var title = Localization.Text("TimeLeft", _language);
        var text = string.Join("\n", (_last?.Lines ?? new List<string> { loading }));
        if (text.Length > 250) text = text[..247] + "...";
        _icon.ShowBalloonTip(10000, title, text, ToolTipIcon.None);
    }

    public void Dispose()
    {
        _timer.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
    }
}
