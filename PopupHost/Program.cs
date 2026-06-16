using System.Text.Json;

namespace PopupHost;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Length < 1) return;

        try
        {
            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(args[0]));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var appName = root.GetProperty("appName").GetString() ?? "";
            var delay = root.GetProperty("delay").GetInt32();
            var message = root.GetProperty("message").GetString() ?? $"{appName} time limit reached!";
            var proc = root.TryGetProperty("proc", out var p) ? p.GetString() ?? "" : "";
            var reason = root.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            var detail = root.TryGetProperty("detail", out var d) ? d.GetString() ?? "" : "";

            Application.Run(new WarningPopup(appName, delay, message, proc, reason, detail));
        }
        catch
        {
            // Invalid args, silently exit
        }
    }
}
