using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MonitorAndControl.Data;

namespace MonitorAndControl.Services;

/// <summary>
/// A button under a phone alert. With only a page it opens that dashboard page (for example
/// "screen-time&amp;grant=15&amp;request=..."). With a decline token it answers "Not now" to a request
/// for more time: one tap in ntfy, or a small confirmation page from Telegram.
/// </summary>
public sealed record PushButton(string Label, string Page = "", string DeclineToken = "");

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

    /// <summary>The dashboard page an alert of this kind should open.</summary>
    public static string PageFor(string eventType) => eventType switch
    {
        var e when e.StartsWith("pc_time", StringComparison.Ordinal) || e == "limits_changed" => "screen-time",
        "new_app" => "limits",
        "watchdog_restart" or "repeated_close" or "database_missing" or "config_missing" or "clock_changed" or "watchdog_unavailable" => "logs",
        "timezone_changed" => "screen-time",
        "login_lockout" => "settings",
        _ => "live"
    };

    // A page name, optionally followed by a few simple parameters: screen-time&grant=15&request=abc
    private static readonly Regex PagePattern = new(@"^[a-z-]{1,20}(&[a-z]{1,10}=[A-Za-z0-9_-]{1,64}){0,3}\z", RegexOptions.Compiled);
    private static readonly Regex TokenPattern = new(@"^[A-Za-z0-9_-]{20,100}\z", RegexOptions.Compiled);

    /// <summary>The dashboard's address without a page ("http://pc:5000" or "https://host/prefix"), or null.</summary>
    public static string? BuildBase(string baseUrl)
    {
        if (!Uri.TryCreate((baseUrl ?? "").Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        var prefix = uri.AbsolutePath.TrimEnd('/');
        if (prefix.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase)) prefix = prefix[..^"/index.html".Length];
        return uri.GetLeftPart(UriPartial.Authority) + prefix;
    }

    public static string? BuildLink(string baseUrl, string? page)
    {
        var root = BuildBase(baseUrl);
        if (root == null) return null;
        var view = page != null && PagePattern.IsMatch(page) ? page : "live";
        return root + "/index.html#" + view;
    }

    private async Task<string?> BaseAsync()
    {
        var custom = (await _db.GetSettingAsync("PublicDashboardUrl", "")).Trim();
        if (custom.Length > 0) return BuildBase(custom);
        var config = Program.GetConfig();
        return config.EnableRemoteDashboard ? BuildBase($"http://{Environment.MachineName}:{config.DashboardPort}") : null;
    }

    private async Task<string?> LinkAsync(string? page)
    {
        var root = await BaseAsync();
        return root == null ? null : BuildLink(root, page);
    }

    /// <summary>ntfy "actions": a page opens in the browser; a decline token is sent straight from the phone.</summary>
    public static List<object> NtfyActions(string root, IEnumerable<PushButton> buttons) =>
        buttons.Take(3).Select(b => TokenPattern.IsMatch(b.DeclineToken)
            ? (object)new
            {
                action = "http", label = b.Label, url = root + "/api/quick/decline", method = "POST",
                headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
                body = JsonSerializer.Serialize(new { token = b.DeclineToken }), clear = true
            }
            : new { action = "view", label = b.Label, url = BuildLink(root, b.Page)! }).ToList();

    /// <summary>Telegram link buttons, one per row. A decline opens a small page that asks before it answers.</summary>
    public static object TelegramKeyboard(string root, IEnumerable<PushButton> buttons) => new
    {
        inline_keyboard = buttons.Take(3).Select(b => new[]
        {
            new { text = b.Label, url = TokenPattern.IsMatch(b.DeclineToken) ? root + "/quick.html#" + b.DeclineToken : BuildLink(root, b.Page)! }
        }).ToArray()
    };

    public async Task<bool> SendAsync(string message, string? page = null, IReadOnlyList<PushButton>? buttons = null)
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
                var root = await BaseAsync();
                var link = root == null ? null : BuildLink(root, page);
                var actions = root == null ? null
                    : buttons is { Count: > 0 } ? NtfyActions(root, buttons)
                    : new List<object> { new { action = "view", label = "Open dashboard", url = link! } };
                object body = link == null
                    ? new { topic, title = "Monitor & Control", message = text, tags = new[] { "shield" } }
                    : new { topic, title = "Monitor & Control", message = text, tags = new[] { "shield" }, click = link, actions };
                var json = JsonSerializer.Serialize(body);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await _http.PostAsync(server, content);
                return response.IsSuccessStatusCode;
            }
            if (provider == "telegram")
            {
                var token = SecretProtector.Unprotect(await _db.GetSettingAsync("PushTelegramToken", ""));
                var chat = await _db.GetSettingAsync("PushTelegramChatId", "");
                if (Validate("telegram", "", "", token, chat) != null) return false;
                var tgRoot = await BaseAsync();
                var tgLink = tgRoot == null ? null : BuildLink(tgRoot, page);
                var tgText = tgLink == null ? text : text + "\n" + tgLink;
                if (tgRoot != null && buttons is { Count: > 0 })
                {
                    // Telegram refuses some addresses (a bare PC name) for buttons; then the alert goes without them.
                    var withButtons = JsonSerializer.Serialize(new { chat_id = chat, text = tgText, reply_markup = TelegramKeyboard(tgRoot, buttons) });
                    using var buttonContent = new StringContent(withButtons, Encoding.UTF8, "application/json");
                    using var buttonResponse = await _http.PostAsync($"https://api.telegram.org/bot{token}/sendMessage", buttonContent);
                    if (buttonResponse.IsSuccessStatusCode) return true;
                }
                var body = JsonSerializer.Serialize(new { chat_id = chat, text = tgText });
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
