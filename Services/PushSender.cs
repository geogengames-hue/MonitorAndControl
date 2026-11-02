using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MonitorAndControl.Data;

namespace MonitorAndControl.Services;

/// <summary>
/// Sends alerts to the parent's phone through ntfy (an app with no account, using a secret topic name) or a
/// Telegram bot. Nothing is installed on the child's PC; only the parent's phone needs the app.
/// </summary>
public sealed class PushSender
{
    public const string DefaultNtfyServer = "https://ntfy.sh";
    private static readonly Regex Topic = new(@"^[A-Za-z0-9_\-]{6,64}$", RegexOptions.Compiled);
    private static readonly Regex ChatId = new(@"^-?\d{1,20}$", RegexOptions.Compiled);
    private static readonly Regex Token = new(@"^\d{5,14}:[A-Za-z0-9_\-]{30,60}$", RegexOptions.Compiled);

    private readonly UsageDatabase _db;
    private readonly HttpClient _http;

    public PushSender(UsageDatabase db)
    {
        _db = db;
        // Redirects are refused: the server address was checked, a redirect target would not be.
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>Returns an explanation of what is wrong with the settings, or null when they are usable.</summary>
    public static string? Validate(string provider, string ntfyServer, string ntfyTopic, string telegramToken, string telegramChatId)
    {
        switch (provider)
        {
            case "": return null;
            case "ntfy":
                if (!Uri.TryCreate(ntfyServer, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                    return "The ntfy server must be an https address, such as https://ntfy.sh.";
                if (!Topic.IsMatch(ntfyTopic))
                    return "The ntfy topic needs 6 to 64 letters, digits, - or _. Make it long and hard to guess: anyone who knows it can read your alerts.";
                return null;
            case "telegram":
                if (!Token.IsMatch(telegramToken)) return "That does not look like a Telegram bot token (it looks like 123456789:AAH...).";
                if (!ChatId.IsMatch(telegramChatId)) return "The Telegram chat ID is a number (it can start with -).";
                return null;
            default: return "Unknown notification service.";
        }
    }

    public async Task<bool> SendAsync(string message)
    {
        try
        {
            var provider = await _db.GetSettingAsync("PushProvider", "");
            if (provider == "") return false;
            var text = $"[{Environment.MachineName}] {message}";
            if (provider == "ntfy")
            {
                var server = (await _db.GetSettingAsync("PushNtfyServer", DefaultNtfyServer)).TrimEnd('/');
                var topic = await _db.GetSettingAsync("PushNtfyTopic", "");
                if (Validate("ntfy", server, topic, "", "") != null || await NotificationService.IsBlockedTargetAsync(new Uri(server))) return false;
                var body = JsonSerializer.Serialize(new { topic, title = "Monitor & Control", message = text, tags = new[] { "shield" } });
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var response = await _http.PostAsync(server, content);
                return response.IsSuccessStatusCode;
            }
            if (provider == "telegram")
            {
                var token = SecretProtector.Unprotect(await _db.GetSettingAsync("PushTelegramToken", ""));
                var chat = await _db.GetSettingAsync("PushTelegramChatId", "");
                if (Validate("telegram", "", "", token, chat) != null) return false;
                var body = JsonSerializer.Serialize(new { chat_id = chat, text });
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var response = await _http.PostAsync($"https://api.telegram.org/bot{token}/sendMessage", content);
                return response.IsSuccessStatusCode;
            }
            return false;
        }
        catch (Exception ex)
        {
            // The token is part of the address, so never log the exception text for it.
            Logger.Instance.Warn("Push notification failed: " + ex.GetType().Name);
            return false;
        }
    }
}
