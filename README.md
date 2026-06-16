# Monitor & Control

A Windows desktop application that monitors application usage, enforces daily time limits, and provides a real‑time web dashboard. It runs silently in the system tray and can optionally be paired with a Windows service watchdog that auto‑restarts it if the process stops.

---

## Features

- **Real‑time usage tracking** — polls the foreground window and accumulates time per app
- **Daily time limits** — set per‑app limits (e.g. Fortnite 120 min/day); apps are auto‑closed when exceeded
- **Graceful countdown** — full‑screen warning popup with countdown before an app is killed
- **Bonus time** — grant extra minutes from the dashboard without changing limits
- **Schedule rules** — define allowed hours per app (e.g. weekdays 15:00–21:00)
- **Auto‑detection** — scan the system for installed games/apps and add them with one click
- **Web dashboard** — responsive SPA with live status, usage charts, history, logs, and settings
- **Multi‑language** — English, German, Spanish, French, Russian (auto‑translates the dashboard UI)
- **Email control** — receive alerts and send commands via Gmail (optional)
- **Webhook alerts** — POST JSON to a custom endpoint on limit breach
- **Watchdog service** — optional Windows service that restarts the monitor if it crashes
- **Admin password** — protect dashboard changes and shutdown
- **Config export/import** — backup and restore limits, schedules, and app mappings
- **Hotkey** — `Ctrl+Alt+H` opens the dashboard

---

## Screenshots

*(Add screenshots here before publishing)*

---

## How It Works

Monitor & Control consists of three executables:

| Component | File | Description |
|-----------|------|-------------|
| **Monitor** | `DeviceMon.exe` | Main application — tracks usage, enforces limits, hosts the web dashboard |
| **Watchdog** | `GameHost.exe` | Optional Windows service that restarts the monitor if it crashes |
| **PopupHost** | `PopupHost.exe` | Child process that shows full‑screen warning popups |

### Data storage

- **SQLite database** at `%LOCALAPPDATA%\SystemHelper\monitor.db` — usage records, limits, schedules, settings
- **Log file** at `%LOCALAPPDATA%\SystemHelper\monitor.log`
- **Configuration** at `appsettings.json` (alongside the exe)

---

## Section A — For Developers: Building from Source

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) (any platform that supports `net8.0-windows`)

### Build & publish

```powershell
# Publish all components (framework‑dependent, ~2 MB each)
.\publish.ps1

# Output goes to: bin\Release\net8.0-windows\win-x64\publish\
```

The script publishes three projects:
1. `MonitorAndControl.csproj` → `DeviceMon.exe`
2. `Watchdog\SystemHelperWatchdog.csproj` → `GameHost.exe`
3. `PopupHost\PopupHost.csproj` → `PopupHost.exe`

It also copies `appsettings.json`, the `wwwroot/` folder, and PowerShell helper scripts.

### Publish options

```powershell
# Self‑contained (includes .NET runtime, ~50 MB, no runtime needed on target)
dotnet publish .\MonitorAndControl.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true

# Framework‑dependent (requires .NET 8 Runtime on target)
dotnet publish .\MonitorAndControl.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

### Project structure

```
MonitorAndControl/
├── Program.cs              # Entry point, DI, startup
├── appsettings.json        # Default configuration
├── Data/
│   └── UsageDatabase.cs    # SQLite data layer
├── Models/                 # Data models (AppConfig, AppLimitRule, ScheduleRule, etc.)
├── Services/
│   ├── Logger.cs           # File‑based logger
│   ├── Localization.cs     # .resx string localization
│   ├── WindowTracker.cs    # Foreground window polling
│   ├── UsageTracker.cs     # Usage accumulation
│   ├── LimitEnforcer.cs    # Limit checking + kill logic
│   ├── SchedulerService.cs # Time‑based schedule enforcement
│   ├── NotificationService.cs  # Webhook notifications
│   ├── EmailService.cs     # Gmail SMTP/IMAP integration
│   ├── HotKeyService.cs    # Global hotkey (Ctrl+Alt+H)
│   ├── PasswordHasher.cs   # PBKDF2 password hashing
│   ├── SecretProtector.cs  # DPAPI credential protection
│   └── DiscoveryService.cs # Scans for installed games/apps
├── UI/
│   └── HiddenForm.cs       # System tray icon + hotkey form
├── Web/
│   ├── DashboardServer.cs  # ASP.NET Minimal API (REST + SSE)
│   └── wwwroot/            # SPA dashboard (HTML, JS, CSS, i18n JSON)
├── Resources/              # .resx localization files
├── PopupHost/              # Warning popup child process
├── Watchdog/               # Windows service watchdog
└── Tests/                  # Unit tests
```

### Running from source

```powershell
dotnet run --project .\MonitorAndControl.csproj
```

Opens the dashboard at `http://localhost:5000` and runs in the system tray.

---

## Section B — For End Users: Using Pre‑Built Releases

### Quick start

1. **Download** the latest release from the [Releases](https://github.com/geogengames-hue/MonitorAndControl/releases) page
2. **Extract** the zip to a folder (e.g. `C:\MonitorAndControl`)
3. **Run `DeviceMon.exe`** (no admin required for basic usage)
4. **Open the dashboard** — press `Ctrl+Alt+H` or open `http://localhost:5000` in a browser
5. **Set limits** — navigate to **Limits** tab, add apps and set daily max minutes
6. **Configure schedule** (optional) — navigate to **Schedule** tab, set allowed hours

For remote access from another PC on the same network, see `appsettings.json` configuration below.

### Configuration (`appsettings.json`)

Place this file alongside `DeviceMon.exe`. All settings are optional — defaults are used when a key is missing.

```json
{
  "DashboardPort": 5000,
  "EnableRemoteDashboard": true,
  "DashboardBindAddress": "0.0.0.0",
  "KillDelaySeconds": 30,
  "ShowWarningOnChildPc": true,
  "PollIntervalMs": 1000,
  "FlushIntervalSec": 30,
  "HotKeyModifiers": "Control+Alt",
  "HotKeyKey": "H",
  "KnownApps": {
    "FortniteClient-Win64-Shipping.exe": "Fortnite",
    "RobloxPlayerBeta.exe": "Roblox",
    "Minecraft.Windows.exe": "Minecraft",
    "chrome.exe": "Chrome"
  },
  "DefaultLimits": [
    { "AppName": "Fortnite", "DailyMaxMinutes": 120 },
    { "AppName": "Roblox", "DailyMaxMinutes": 90 }
  ],
  "Schedule": [
    { "DayOfWeek": "Weekday", "StartTime": "15:00", "EndTime": "21:00" },
    { "DayOfWeek": "Weekend", "StartTime": "09:00", "EndTime": "22:00" }
  ]
}
```

| Key | Default | Description |
|-----|---------|-------------|
| `DashboardPort` | `5000` | Port for the web dashboard |
| `EnableRemoteDashboard` | `false` | Allow access from other PCs on the network |
| `DashboardBindAddress` | `127.0.0.1` | Bind address (`0.0.0.0` for all interfaces) |
| `KillDelaySeconds` | `30` | Seconds between limit breach warning and closing the app |
| `ShowWarningOnChildPc` | `true` | Show full‑screen popup before killing an app |
| `PollIntervalMs` | `1000` | Foreground window check interval (ms) |
| `FlushIntervalSec` | `30` | How often usage data is written to the database |
| `HotKeyModifiers` | `Control+Alt` | Modifier keys for the dashboard hotkey |
| `HotKeyKey` | `H` | Key for the dashboard hotkey |
| `KnownApps` | `{}` | Map of process names → friendly app names |
| `DefaultLimits` | `[]` | Default daily limits for known apps |
| `Schedule` | `[]` | Default allowed hours schedule |

> **Security note:** When `EnableRemoteDashboard` is `true`, anyone on your local network can access the dashboard. Set an admin password in the dashboard **Settings** tab to protect changes.

### Installing the Watchdog (optional, requires admin)

The watchdog (`GameHost.exe`) runs as a Windows service that restarts `DeviceMon.exe` if it stops.

```powershell
# Run PowerShell as Administrator, then:
.\install-watchdog.ps1

# Or specify a custom publish folder:
.\install-watchdog.ps1 -PublishDir "C:\MonitorAndControl"
```

To remove:

```powershell
.\GameHost.exe --uninstall
# or
.\uninstall-watchdog.ps1
```

### Using the dashboard

**Navigation:**

| Tab | Description |
|-----|-------------|
| **Live** | Currently active app, quick actions (pause, resume, block all, extend time) |
| **Today** | Bar chart and table of today's app usage |
| **History** | Historical usage with filters, charts, and stats (7/14/30/90 days or custom range) |
| **Limits** | Per‑app daily limits, bonus time, status, edit/add/remove/forget apps |
| **Discover** | Scan system for installed games/apps, add them with default limit |
| **Schedule** | Time‑based allowed hours rules per app |
| **Logs** | Real‑time event log with filtering |
| **Settings** | Kill delay, language, auto‑start, webhook, email, admin password, backup/restore, health |

**Admin password:** Set one in **Settings** to protect dashboard changes (pause, resume, reset, kill, settings edits) and shutdown.

**Remote access:** Settings show the computer name and IP addresses. From another PC, open `http://CHILD_PC_NAME:5000` or `http://192.168.x.x:5000`.

### Email control (optional)

1. Enable 2‑Factor Authentication on your Gmail account
2. Generate an [App Password](https://support.google.com/accounts/answer/185833)
3. Configure in **Settings** → **Email Notifications & Control**
4. Reply to alerts with commands: `mc: status`, `mc: set [app] [min] min`, `mc: pause`, `mc: resume`

### Uninstallation

1. Remove the watchdog (if installed): run `GameHost.exe --uninstall` as Administrator
2. Delete the application folder
3. (Optional) Delete data: `%LOCALAPPDATA%\SystemHelper\`

---

## Translations

Supports English, German, Spanish, French, and Russian.

- **Dashboard UI:** Translated via JSON files in `Web/wwwroot/i18n/`
- **Warning popups:** Translated via `.resx` files in `Resources/`
- The language can be changed in **Settings** → **Language** (reloads the page)

To add a new language:
1. Copy `Web/wwwroot/i18n/en.json` → `Web/wwwroot/i18n/{code}.json` and translate the values
2. Copy `Resources/Strings.resx` → `Resources/Strings.{code}.resx` and translate the values
3. Add the option to the language `<select>` in `Web/wwwroot/index.html`

---

## FAQ / Troubleshooting

**Q: The dashboard shows "?" instead of accented characters.**
A: Your JSON translation files may have been saved with the wrong encoding. Ensure all files in `Web/wwwroot/i18n/` are saved as **UTF-8**.

**Q: The watchdog service won't install.**
A: Run PowerShell **as Administrator**. The service requires elevation to create a Windows service.

**Q: I can't access the dashboard from another PC.**
A: Set `EnableRemoteDashboard` to `true` and `DashboardBindAddress` to `"0.0.0.0"` in `appsettings.json`. Ensure the Windows Firewall allows port 5000.

**Q: How do I reset all usage data?**
A: Delete the SQLite database at `%LOCALAPPDATA%\SystemHelper\monitor.db` while the monitor is not running. It will be recreated on next launch.

**Q: Where are logs stored?**
A: `%LOCALAPPDATA%\SystemHelper\monitor.log` for the main app, and `C:\ProgramData\SystemHelper\watchdog.log` for the watchdog service.

**Q: Can I run it without the web dashboard?**
A: No. The web dashboard is the primary UI.

---

## License

Fair Use License — see the [LICENSE](LICENSE) file for details.

- ✅ Free for personal, educational, and non‑commercial use
- ✅ Modify and distribute with attribution
- ❌ No selling, commercial redistribution, or republishing on app stores/public repositories
