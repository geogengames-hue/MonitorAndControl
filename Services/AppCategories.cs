using System.Collections.Concurrent;
using MonitorAndControl.Data;
using MonitorAndControl.Models;

namespace MonitorAndControl.Services;

/// <summary>
/// App categories (Games, Social, Video, ...), so a group such as "Games 1 h" also covers a game that is new on the PC.
/// An app's category is the parent's choice if they made one, else a guess from its name and where it is installed.
/// An app listed by name in a group belongs to that group only; the category is for everything else.
/// </summary>
public sealed class AppCategories
{
    public static readonly string[] Names = { "games", "social", "video", "browsers", "school" };
    /// <summary>The parent's choice "no category" (the guess is not used).</summary>
    public const string None = "none";
    public const int MaxOverrides = 500;

    private static readonly Dictionary<string, string> KnownNames = Build(new()
    {
        ["games"] = new[]
        {
            "minecraft", "minecraftlauncher", "minecraft.windows", "robloxplayerbeta", "roblox", "robloxstudiobeta", "fortniteclient-win64-shipping",
            "fortnite", "valorant", "valorant-win64-shipping", "leagueclient", "league of legends", "riotclientservices", "steam", "steamwebhelper",
            "epicgameslauncher", "gta5", "gtavlauncher", "csgo", "cs2", "dota2", "overwatch", "rocketleague", "fallguys_client",
            "among us", "terraria", "genshinimpact", "osu!", "battle.net", "r5apex", "tslgame", "eldenring", "rainbowsix", "fifa",
            "eafc", "eadesktop", "origin", "ubisoftconnect", "upc", "goggalaxy", "xboxpcapp", "brawlhalla", "warframe.x64",
            "hollow_knight", "stardew valley", "geometrydash", "subnautica", "rust", "pubg", "apex legends", "candy crush saga", "solitaire"
        },
        ["social"] = new[]
        {
            "discord", "telegram", "whatsapp", "whatsapp.root", "instagram", "tiktok", "snapchat", "facebook", "messenger", "signal",
            "skype", "viber", "wechat", "line", "kik", "threads", "x", "twitter", "reddit", "vk"
        },
        ["video"] = new[]
        {
            "vlc", "netflix", "youtube", "twitch", "primevideo", "prime video", "disney+", "disneyplus", "plex", "kodi", "mpc-hc", "mpc-hc64",
            "potplayer", "potplayermini64", "hulu", "crunchyroll"
        },
        ["browsers"] = new[]
        {
            "chrome", "google chrome", "msedge", "microsoft edge", "firefox", "opera", "opera gx", "brave", "vivaldi", "iexplore", "arc", "yandex", "browser"
        },
        ["school"] = new[]
        {
            "winword", "word", "excel", "powerpnt", "powerpoint", "onenote", "teams", "ms-teams", "acrord32", "acrobat", "scratch", "scratch 3",
            "geogebra", "duolingo", "khan academy", "zoom", "notion", "libreoffice", "soffice", "swriter"
        }
    });

    // Folders games are installed in by the usual stores and launchers.
    private static readonly string[] GameFolders =
    {
        @"\steamapps\common\", @"\epic games\", @"\riot games\", @"\xboxgames\", @"\gog galaxy\games\", @"\gog games\",
        @"\ubisoft game launcher\games\", @"\ea games\", @"\roblox\versions\", @"\minecraft launcher\", @"\battle.net\"
    };

    private static Dictionary<string, string> Build(Dictionary<string, string[]> lists)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (category, names) in lists)
            foreach (var name in names) result.TryAdd(name, category);
        return result;
    }

    public static bool IsValid(string? category) => category != null && (category == None || Names.Contains(category));

    /// <summary>The guess from the app's name, process name and installed location, or null.</summary>
    public static string? Guess(string appName, string? processName, string? path)
    {
        var process = Path.GetFileNameWithoutExtension(processName ?? "");
        if (KnownNames.TryGetValue(appName, out var byApp)) return byApp;
        if (process.Length > 0 && KnownNames.TryGetValue(process, out var byProcess)) return byProcess;
        if (!string.IsNullOrEmpty(path))
        {
            var lower = path.Replace('/', '\\').ToLowerInvariant();
            if (GameFolders.Any(lower.Contains)) return "games";
        }
        return null;
    }

    /// <summary>
    /// The groups whose time an app uses: the groups that list it by name, or, if none does, the groups for its category.
    /// </summary>
    public static IEnumerable<AppLimitGroup> GroupsFor(string appName, string? category, IReadOnlyCollection<AppLimitGroup> groups)
    {
        var named = groups.Where(g => g.AppNames.Contains(appName, StringComparer.OrdinalIgnoreCase)).ToList();
        // A group that is turned off does not take the app away from its category group.
        if (named.Any(g => g.Enabled) || string.IsNullOrEmpty(category)) return named;
        return named.Concat(groups.Where(g => g.Category == category && !named.Contains(g)));
    }

    private volatile Dictionary<string, string> _overrides = new(StringComparer.OrdinalIgnoreCase);
    // The guess for each app seen in this session (looked up once: it reads where the program is installed).
    private readonly ConcurrentDictionary<string, string> _guesses = new(StringComparer.OrdinalIgnoreCase);
    // The process each app ran as in this session, so a category group can close an app that has no mapping.
    private readonly ConcurrentDictionary<string, string> _processes = new(StringComparer.OrdinalIgnoreCase);
    // Apps whose program file could not be read (elevated, just closed): looked at again after a while.
    private readonly ConcurrentDictionary<string, DateTime> _retryAt = new(StringComparer.OrdinalIgnoreCase);

    public async Task ReloadAsync(UsageDatabase db) =>
        _overrides = new Dictionary<string, string>(await db.GetAppCategoriesAsync(), StringComparer.OrdinalIgnoreCase);

    /// <summary>The app's category, or null for none. The first time an app is seen its program file is looked at.</summary>
    public string? CategoryOf(string appName, string? processName)
    {
        if (!string.IsNullOrEmpty(processName) && _processes.Count < 2000) _processes[appName] = processName;
        if (_overrides.TryGetValue(appName, out var chosen)) return chosen == None ? null : chosen;
        if (_guesses.TryGetValue(appName, out var guess)) return guess.Length > 0 ? guess : null;
        guess = Guess(appName, processName, null) ?? "";
        if (guess.Length == 0 && !string.IsNullOrEmpty(processName))
        {
            if (_retryAt.TryGetValue(appName, out var retry) && DateTime.UtcNow < retry) return null;
            var path = ProgramGate.ProgramPath(processName);
            if (path == null)
            {
                _retryAt[appName] = DateTime.UtcNow.AddSeconds(30);
                return null;
            }
            _retryAt.TryRemove(appName, out _);
            guess = Guess(appName, processName, path) ?? "";
        }
        // An app seen without a process (from the usage list) is guessed again once it runs.
        if (!string.IsNullOrEmpty(processName) || guess.Length > 0) _guesses[appName] = guess;
        return guess.Length > 0 ? guess : null;
    }

    /// <summary>The apps seen running in this session, with their process.</summary>
    public IReadOnlyDictionary<string, string> SeenApps() => new Dictionary<string, string>(_processes, StringComparer.OrdinalIgnoreCase);

    /// <summary>The apps whose category was guessed in this session (for the Limits page).</summary>
    public IReadOnlyDictionary<string, string> Guesses() =>
        _guesses.Where(g => g.Value.Length > 0).ToDictionary(g => g.Key, g => g.Value, StringComparer.OrdinalIgnoreCase);
}
