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
│   ├── WeeklyReport.cs          # WeeklyHistoryStore + ContentFilter + HTML report generator
│   └── WindowMonitor.cs        # Detects active browser + extracts site from title
│
├── ParentalGuard.Service/       # Windows Service - proxy enforcement only, see note below
│   └── ParentalGuardService.cs  # Hosts proxy, watchdog (15s), blocklist reload (10s), external refresh (5min delay + 24h cycle)
│
└── ParentalGuard.UI/            # WPF management UI - also owns all activity tracking, see note below
    ├── App.xaml / App.xaml.cs   # Owns WindowMonitor + wires it into UsageTracker/FrictionEngine/WeeklyHistoryStore
    ├── MainWindow.xaml          # 6-tab UI: Activity Log, Screen Time, Blocked Domains,
    ├── MainWindow.xaml.cs       #           Keywords, Edu Whitelist, Settings
    ├── HardBlockWindow.xaml(.cs)      # Full-screen 60-minute block, PIN override
    ├── AutoBlockNotifyWindow.xaml(.cs) # 30/30 auto-block + friction nudge toast
    ├── PinDialog.xaml           # Reusable PIN entry dialog
    └── PinDialog.xaml.cs
```

**Where tracking actually lives, and why**: this was originally split the other way (Service tracks, UI reads) but that design was empirically wrong - see "Session 0 confirmed" below. `ParentalGuard.UI` is now the sole writer of `usage.json`/`weekly_history.json` (via the `WindowMonitor` it owns in `App.xaml.cs`) and of auto-block entries in `blocklist.json`. `ParentalGuard.Service` only enforces the proxy and re-reads `blocklist.json` every 10s to pick up whatever the UI adds. Practical implication: usage tracking, friction nudges, and auto-blocking only happen while `ParentalGuard.UI` is running - if you want that to hold even when the user hasn't opened the window, auto-launch the UI at logon (Startup folder shortcut or a `HKCU\...\Run` registry entry) rather than relying on the service for it.

---

## NuGet Packages Required

### ParentalGuard.Core

```xml
<PackageReference Include="Microsoft.Data.Sqlite" Version="8.0.0" />
<PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
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
| `override.json` | Temporary override expiry timestamp (see Temporary Override below) |

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
- 60 min: hard block - full-screen `HardBlockWindow`, PIN-gated "Parent Override" button

### Temporary Override

- Two entry points, both PIN-gated then prompt for a duration (30 min / 1 hr / 2 hr) via `OverrideDurationDialog`:
  - The "Parent Override" button on `HardBlockWindow` itself (reactive - kid hits the wall, parent grants relief)
  - "Grant Temporary Override" in the Settings tab (proactive - parent grants extra time before the block hits; closes an open `HardBlockWindow` too, via `HardBlockWindow.GrantedExternally()`)
- `FrictionEngine.GrantOverride(TimeSpan)` sets an `OverrideUntilUtc` timestamp, persisted to `override.json` so it survives a UI restart mid-override
- While active, `FrictionEngine.Evaluate()` short-circuits to `FrictionPhase.Free` - no nudges, no hard block - regardless of total browser seconds
- Cleared automatically on day rollover (`FrictionEngine.ResetForNewDay()`)
- Scope is deliberately narrow: it only suspends the *time-based* friction ladder, not the domain/keyword blocklist - granting extra screen time doesn't unblock adult content
- Earlier bug this replaced: the original "Parent Override" button just PIN-gated a `Close()` with no state change, so `FrictionEngine.Evaluate()` still saw 60+ minutes on the very next heartbeat (~2s later) and popped the hard-block window right back up - there was no real override, just a ~2s flicker

### Educational Whitelist

Built-in sites (in `EducationalWhitelist.cs`):

khanacademy.org, wikipedia.org, quizlet.com, duolingo.com, coursera.org, edx.org,
wolframalpha.com, desmos.com, ck12.org, collegeboard.org, commonlit.org, sparknotes.com,
cliffsnotes.com, scholar.google.com, jstor.org, codecademy.com, stackoverflow.com,
github.com, w3schools.com, mathway.com, symbolab.com, prepscholar.com

User can add custom sites via the Edu Whitelist tab in the UI.

### Proxy (Port 8877)

- Runs as Windows Service via `ParentalGuardService` - the service does *only* this now (plus blocklist reload/refresh); it does not track usage, see the architecture note above
- Intercepts HTTP traffic, checks host + URL against `BlocklistManager`
- Serves 403 block page for blocked requests
- HTTPS: only blocks by domain (no SSL inspection — CONNECT tunnel passthrough)
- StevenBlack porn list fetched 5 minutes after service start, then every 24 hours
- Reloads `blocklist.json` every 10s so auto-blocks added by `ParentalGuard.UI`'s tracking take effect promptly

### Weekly Report

- Self-contained HTML file (`WeeklyReportGenerator`) - no PDF library, opens directly in the default browser via `Process.Start` with `UseShellExecute = true`
- PIN-gated in the UI
- Filters adult/gambling content via `ContentFilter` before generating
- Shows: total time, top site, auto-blocked sites, daily breakdown with visit counts
- All dynamic values (site names, etc.) are HTML-encoded via `WebUtility.HtmlEncode` before being embedded
- Saved to `%Documents%\ParentalGuardReports\` as `WeeklyReport_yyyy-MM-dd.html`

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
3. **WindowMonitor connected and running** — owned by `ParentalGuard.UI/App.xaml.cs`, which wires `WindowMonitor.SiteChanged`/`Heartbeat` into `UsageTracker`, evaluates `FrictionEngine` on every heartbeat, and wires `UsageTracker.DayRolledOver` into `WeeklyHistoryStore` + `FrictionEngine.ResetForNewDay()`. Originally this was wired into `ParentalGuardService` instead - see below for why that moved.

### Session 0 confirmed: WindowMonitor cannot run in the Windows Service

This was flagged as "not yet verified on real Windows" and has since been tested empirically. `ParentalGuard.SessionZeroProbe` (a throwaway diagnostic worker, since deleted) was installed as a **real Windows Service** on a `windows-latest` GitHub Actions runner and polled `GetForegroundWindow` / `WindowMonitor.TryGetActiveBrowserSite()` every 2s for 40s, while a real Edge window was open and active in the runner's interactive session the whole time (confirmed via 5 orphaned `msedge` processes still running at job cleanup).

Result: **20 out of 20 polls returned a null window handle** (`hwnd=0x0`, empty title, `TryGetActiveBrowserSite()` = null). The probe's own session (`CurrentProcess.SessionId=0`) never matched the runner's active console session (`WTSGetActiveConsoleSessionId=2`). Session 0 isolation is total - a Windows Service genuinely cannot see the interactive user's foreground window, full stop.

**Consequence**: `ParentalGuardService` no longer runs `WindowMonitor`/`UsageTracker`/`FrictionEngine` at all - it was pure dead code that would never fire. It's down to proxy enforcement plus a 10s `blocklist.json` reload (see architecture note above). All tracking now lives in `ParentalGuard.UI`, which runs in the interactive session where `GetForegroundWindow` actually works. `MainWindow` no longer needs to `Reload()` state from disk either, since it shares the same in-process `UsageTracker`/`BlocklistManager` instances that `App`'s `WindowMonitor` writes to directly - `AutoBlockTriggered` is now handled as a direct event subscription instead of a polling diff.

**Follow-up worth doing**: since tracking now depends on `ParentalGuard.UI` actually running, set it up to auto-launch at user logon (Startup folder shortcut, or `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`) so coverage matches the original intent of "tracking works even when the user hasn't opened the window." Not yet implemented.

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
| Tracking requires ParentalGuard.UI to be running | Confirmed the Windows Service can't do it (Session 0 isolation - see above); no auto-launch-at-logon set up yet |
| Proxy bypass possible | Lock via Group Policy or registry ACL |
| Single machine config | No central management across Neil + Nayla's machines |
