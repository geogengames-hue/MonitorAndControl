using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MonitorAndControl.Data;

namespace MonitorAndControl.Services;

public class NotificationService : IDisposable
{
    private readonly UsageDatabase _db;
    private readonly HttpClient _http;
    private string? _cachedWebhookUrl;
    private DateTime _lastUrlCheck = DateTime.MinValue;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public NotificationService(UsageDatabase db)
    {
        _db = db;
        _http = new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(10);
    }

    public async Task NotifyBreachAsync(string appName, int killDelaySeconds)
    {
        await FireWebhook("limit_breach", appName, new
        {
            type = "limit_breach",
            app = appName,
            message = $"{appName} time limit reached. Will be closed in {killDelaySeconds}s.",
            killDelaySeconds,
            timestamp = DateTime.Now
        });
    }

    public async Task NotifyKilledAsync(string appName)
    {
        await FireWebhook("app_killed", appName, new
        {
            type = "app_killed",
            app = appName,
            message = $"{appName} was closed due to time limit.",
            timestamp = DateTime.Now
        });
    }

    public async Task NotifyScheduleTerminatedAsync(string appName)
    {
        await FireWebhook("schedule_kill", appName, new
        {
            type = "schedule_kill",
            app = appName,
            message = $"{appName} was closed by schedule rule.",
            timestamp = DateTime.Now
        });
    }

    private async Task FireWebhook(string eventType, string appName, object payload)
    {
        try
        {
            var url = await GetWebhookUrlAsync();
            if (string.IsNullOrWhiteSpace(url)) return;

            var json = JsonSerializer.Serialize(payload, JsonOpts);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _http.PostAsync(url, content);
            if (!response.IsSuccessStatusCode)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Webhook failed ({response.StatusCode}) for event {eventType}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Webhook error: {ex.Message}");
        }
    }

    public async Task TestWebhookAsync()
    {
        await FireWebhook("test", "System", new
        {
            type = "test",
            app = "System",
            message = "Monitor & Control webhook test - notification working!",
            timestamp = DateTime.Now
        });
    }

    private async Task<string?> GetWebhookUrlAsync()
    {
        if ((DateTime.UtcNow - _lastUrlCheck).TotalSeconds > 30)
        {
            _cachedWebhookUrl = await _db.GetSettingAsync("WebhookUrl", "");
            _lastUrlCheck = DateTime.UtcNow;
        }
        return string.IsNullOrWhiteSpace(_cachedWebhookUrl) ? null : _cachedWebhookUrl;
    }

    public void InvalidateCache()
    {
        _lastUrlCheck = DateTime.MinValue;
    }

    public void Dispose()
    {
        _http?.Dispose();
    }
}
