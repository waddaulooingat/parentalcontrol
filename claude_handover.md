# Parental Guard — Claude Code Handover

## What This Is

A combined parental control app for Windows that merges two previously separate tools:

- **WinResMonitor** — local HTTP proxy that blocks adult/unwanted websites
- **Screen Time Monitor** — tracks browser usage, confronts with messages, auto-blocks overused sites

## Architecture — 3 Projects

```
ParentalGuard/
├── ParentalGuard.Core/          # Shared logic (proxy, tracker, blocklist, reports)
│   ├── BlocklistManager.cs      # Domain + keyword blocklist, auto-block entries
│   ├── EducationalWhitelist.cs  # Sites exempt from 30/30 auto-block rule
│   ├── FrictionEngine.cs        # Escalating confrontational messages
│   ├── Helpers.cs               # PinStore + BlocklistFetcher (StevenBlack)
│   ├── Logger.cs                # SQLite request logger
│   ├── ProxyEngine.cs           # HttpListener local proxy on port 8877
│   ├── UsageTracker.cs          # Visit counting + time tracking + auto-block trigger
│   ├── WeeklyReport.cs          # WeeklyHistoryStore + ContentFilter + PDF generator
│   └── WindowMonitor.cs        # Detects active browser + extracts site from title
│
├── ParentalGuard.Service/       # Windows Service
│   └── ParentalGuardService.cs  # Hosts proxy, watchdog (15s), blocklist refresh (5min delay + 24h cycle)
│
└── ParentalGuard.UI/            # WPF management UI
    ├── App.xaml / App.xaml.cs
    ├── MainWindow.xaml          # 6-tab UI: Activity Log, Screen Time, Blocked Domains,
    ├── MainWindow.xaml.cs       #           Keywords, Edu Whitelist, Settings
    ├── PinDialog.xaml           # Reusable PIN entry dialog
    └── PinDialog.xaml.cs
```

---

## NuGet Packages Required

### ParentalGuard.Core

```xml
<PackageReference Include="Microsoft.Data.Sqlite" Version="8.0.0" />
<PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
<PackageReference Include="PdfSharp" Version="6.0.0" />
```

### ParentalGuard.Service

```xml
<PackageReference Include="System.ServiceProcess.ServiceController" Version="8.0.0" />
```

---

## Data Files — all under `%ProgramData%\ParentalGuard\`

| File | Purpose |
|---|---|
| `blocklist.json` | Blocked domains + keywords + auto-block entries with expiry |
| `whitelist.json` | User-added educational domains (built-ins are in code) |
| `usage.json` | Today's per-site visit count + accumulated seconds |
| `weekly_history.json` | Rolling 35-day usage history |
| `requests.db` | SQLite — every proxy request logged |
| `pin.dat` | Parent PIN stored as binary string |

---

## Key Business Logic

### Auto-Block Rule (30/30)

- Every browser title change to a non-whitelisted site = +1 visit
- Hit 30 visits OR 30 minutes on that site → auto-add to blocklist with midnight expiry
- Show full-screen block message: "You have visited [site] 30 times today. Blocked for today."
- At midnight (day rollover in `UsageTracker.ResetIfNewDay()`): expired auto-blocks are cleared, usage resets

### Friction Phase Ladder (FrictionEngine.cs)

- 0–30 min: free browsing, no messages
- 30–45 min: casual confrontational nudges ("Come on bro...", "Bro. Put it down.")
- 45–55 min: escalated messages ("45 minutes. Are you serious?")
- 55–60 min: final warning messages ("Five minutes to hard block. Walk away.")
- 60 min: hard block (currently triggers event — wire up a full-screen WPF block window)

### Educational Whitelist

Built-in sites (in `EducationalWhitelist.cs`):

khanacademy.org, wikipedia.org, quizlet.com, duolingo.com, coursera.org, edx.org,
wolframalpha.com, desmos.com, ck12.org, collegeboard.org, commonlit.org, sparknotes.com,
cliffsnotes.com, scholar.google.com, jstor.org, codecademy.com, stackoverflow.com,
github.com, w3schools.com, mathway.com, symbolab.com, prepscholar.com

User can add custom sites via the Edu Whitelist tab in the UI.

### Proxy (Port 8877)

- Runs as Windows Service via `ParentalGuardService`
- Intercepts HTTP traffic, checks host + URL against `BlocklistManager`
- Serves 403 block page for blocked requests
- HTTPS: only blocks by domain (no SSL inspection — CONNECT tunnel passthrough)
- StevenBlack porn list fetched 5 minutes after service start, then every 24 hours

### Weekly PDF Report

- PIN-gated in the UI
- Filters adult/gambling content via `ContentFilter` before generating
- Shows: total time, top site, auto-blocked sites, daily breakdown with visit counts
- No emoji (avoids PdfSharp font resolver issues)
- Saved to `%Documents%\ParentalGuardReports\`

---

## Build & Deploy

### Build

```
dotnet build ParentalGuard.sln -c Release
```

### Install Windows Service (run as Admin)

```cmd
sc create ParentalGuard binPath= "C:\Program Files\ParentalGuard\ParentalGuard.Service.exe" start= auto DisplayName= "Parental Guard"
sc start ParentalGuard
```

Or use the Settings tab in the WPF UI — it runs `sc.exe` with UAC elevation.

### Configure Browser Proxy

Set system proxy or per-browser proxy to `127.0.0.1:8877`.

PowerShell (run as Admin):

```powershell
Set-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings" -Name ProxyEnable -Value 1
Set-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings" -Name ProxyServer -Value "127.0.0.1:8877"
```

Lock proxy settings (prevent bypass):

```cmd
reg add "HKCU\Software\Policies\Microsoft\Internet Explorer\Control Panel" /v Proxy /t REG_DWORD /d 1 /f
```

---

## What Still Needs Wiring

### Done

1. **Full-screen hard block WPF window** — `ParentalGuard.UI/HardBlockWindow.xaml(.cs)`: maximized, borderless, dark background, "Parent Override" button gated by `PinDialog`. `MainWindow` subscribes to `FrictionEngine.HardBlockTriggered` and shows it (guarding against opening a second one while it's already up).
2. **Auto-block notification window** — `ParentalGuard.UI/AutoBlockNotifyWindow.xaml(.cs)`: a bottom-right toast, auto-dismisses after 8s or on click. `AutoBlockNotifyWindow.ForAutoBlock(site, visits)` renders the exact required copy: *"You have visited [site] [N] times today. Blocked for today."* The same window (via its plain message constructor) is reused for `FrictionEngine.NudgeRequested` toasts.
3. **WindowMonitor connected in the service** — `ParentalGuardService` instantiates `WindowMonitor`, `UsageTracker`, and `FrictionEngine`, wires `SiteChanged`/`Heartbeat` into the tracker, and runs a 10s `PeriodicTimer` loop calling `_tracker.ResetIfNewDay()` + `_friction.Evaluate(_tracker.TotalBrowserSeconds)` (see `FrictionLoopAsync`).

Because the service normally runs in Session 0, isolated from the interactive desktop, it cannot itself pop WPF windows — see the caveat comment on `ParentalGuardService`. The service is the single writer of `usage.json`/`blocklist.json`; `ParentalGuard.UI` reloads those files (`BlocklistManager.Reload()` / `UsageTracker.Reload()`) and is the one that actually shows the hard-block/auto-block/nudge windows, via a 5s polling loop in `MainWindow`. **Not yet verified on real Windows**: whether `WindowMonitor.TryGetActiveBrowserSite()` can see the logged-in user's foreground window from a Session 0 service at all. If it can't, move the `WindowMonitor` instantiation from `ParentalGuardService` into `ParentalGuard.UI` (or a logon-triggered Scheduled Task) instead — the event wiring is identical either way.

### 4. PsatGateChecker (optional — carry from old project)

If you want the PSAT gate (blocks browsing until daily PSAT goal is met), port `PsatGateChecker.cs` from the old ScreenTimeMonitor project. Reads from `%LOCALAPPDATA%\PSATPrep\engagement.json`.

### 5. Per-child profiles (Neil vs Nayla)

Currently all config is shared. To support both kids on different machines with different rules, add a `ProfileName` field to config and a profile selector in the UI. Simple — one JSON config per profile in `%ProgramData%\ParentalGuard\profiles\`.

---

## Known Limitations

| Issue | Notes |
|---|---|
| HTTPS content not inspected | Only domain blocked, not URL path keywords |
| WindowMonitor parses titles heuristically | May miss some sites or misidentify others |
| Proxy bypass possible | Lock via Group Policy or registry ACL |
| Single machine config | No central management across Neil + Nayla's machines |
