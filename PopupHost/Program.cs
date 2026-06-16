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
            var closingTemplate = root.TryGetProperty("closingTemplate", out var ct)
                ? ct.GetString() ?? "Closing {app} in {seconds} seconds..."
                : "Closing {app} in {seconds} seconds...";
            var closedTemplate = root.TryGetProperty("closedTemplate", out var cl)
                ? cl.GetString() ?? "{app} was closed."
                : "{app} was closed.";
            var closingNowText = root.TryGetProperty("closingNowText", out var cn)
                ? cn.GetString() ?? "Closing..."
                : "Closing...";

            Application.Run(new WarningPopup(
                appName,
                delay,
                message,
                proc,
                reason,
                detail,
                closingTemplate,
                closedTemplate,
                closingNowText));
        }
        catch (Exception ex)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "SystemHelper_popup_error.log"),
                    $"{DateTime.Now}: {ex}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
