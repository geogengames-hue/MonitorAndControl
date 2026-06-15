using System.Text.Json;

namespace PopupHost;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Length < 1) return;

        var input = string.Join(" ", args);
        try
        {
            using var doc = JsonDocument.Parse(input);
            var root = doc.RootElement;
            var appName = root.GetProperty("appName").GetString() ?? "";
            var delay = root.GetProperty("delay").GetInt32();
            var message = root.GetProperty("message").GetString() ?? $"{appName} time limit reached!";

            Application.Run(new WarningPopup(appName, delay, message));
        }
        catch
        {
            // Invalid args, silently exit
        }
    }
}
