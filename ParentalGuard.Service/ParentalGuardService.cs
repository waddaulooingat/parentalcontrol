using ParentalGuard.Core;

namespace ParentalGuard.Service;

/// <summary>
/// Hosts the local blocking proxy plus background usage tracking. Runs the proxy watchdog
/// every 15s, refreshes the external (StevenBlack) blocklist 5 minutes after startup and
/// every 24h thereafter, and evaluates the friction ladder every 10s.
/// </summary>
/// <remarks>
/// A Windows Service normally runs in Session 0, isolated from the interactive desktop, so
/// <see cref="WindowMonitor"/> may not be able to see the logged-in user's foreground window
/// and this process cannot show WPF windows on their desktop. The proxy works regardless
/// (it operates purely at the network layer). ParentalGuard.UI is responsible for presenting
/// friction nudges, the hard-block window, and auto-block notifications - it reads the same
/// %ProgramData%\ParentalGuard\ state files this service writes, so tracking still accrues
/// even while the UI is closed. If GetForegroundWindow proves unavailable from Session 0 in
/// practice, run WindowMonitor from the UI (or a logon-triggered Scheduled Task) instead.
/// </remarks>
public class ParentalGuardService : BackgroundService
{
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FrictionEvaluationInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan InitialBlocklistRefreshDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan BlocklistRefreshInterval = TimeSpan.FromHours(24);

    private readonly ILogger<ParentalGuardService> _logger;

    private readonly BlocklistManager _blocklist;
    private readonly EducationalWhitelist _whitelist;
    private readonly Core.Logger _requestLogger;
    private readonly UsageTracker _tracker;
    private readonly FrictionEngine _friction;
    private readonly WindowMonitor _monitor;
    private readonly WeeklyHistoryStore _history;
    private readonly ProxyEngine _proxy;
    private readonly BlocklistFetcher _blocklistFetcher;

    public ParentalGuardService(ILogger<ParentalGuardService> logger)
    {
        _logger = logger;

        AppPaths.EnsureDirectoriesExist();

        _blocklist = new BlocklistManager();
        _whitelist = new EducationalWhitelist();
        _requestLogger = new Core.Logger();
        _tracker = new UsageTracker(_whitelist, _blocklist);
        _friction = new FrictionEngine();
        _monitor = new WindowMonitor();
        _history = new WeeklyHistoryStore();
        _proxy = new ProxyEngine(_blocklist, _requestLogger);
        _blocklistFetcher = new BlocklistFetcher();

        _monitor.SiteChanged += _tracker.OnSiteChanged;
        _monitor.Heartbeat += OnHeartbeat;
        _tracker.AutoBlockTriggered += OnAutoBlockTriggered;
        _tracker.DayRolledOver += OnDayRolledOver;
        _friction.NudgeRequested += OnNudgeRequested;
        _friction.HardBlockTriggered += OnHardBlockTriggered;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _proxy.Start();
        _logger.LogInformation("Proxy engine listening on 127.0.0.1:8877");

        _monitor.Start();
        _logger.LogInformation("Window monitor started");

        var refreshLoop = RefreshBlocklistLoopAsync(stoppingToken);

        using var watchdogTimer = new PeriodicTimer(WatchdogInterval);
        using var frictionTimer = new PeriodicTimer(FrictionEvaluationInterval);

        var watchdogLoop = WatchdogLoopAsync(watchdogTimer, stoppingToken);
        var frictionLoop = FrictionLoopAsync(frictionTimer, stoppingToken);

        await Task.WhenAll(watchdogLoop, frictionLoop, refreshLoop);
    }

    private async Task WatchdogLoopAsync(PeriodicTimer timer, CancellationToken token)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                if (!_proxy.IsRunning)
                {
                    _logger.LogWarning("Proxy engine was not running; restarting");
                    _proxy.Start();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task FrictionLoopAsync(PeriodicTimer timer, CancellationToken token)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                _tracker.ResetIfNewDay();
                _friction.Evaluate(_tracker.TotalBrowserSeconds);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RefreshBlocklistLoopAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(InitialBlocklistRefreshDelay, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!token.IsCancellationRequested)
        {
            try
            {
                var count = await _blocklistFetcher.RefreshAsync(_blocklist, token);
                _logger.LogInformation("Refreshed external blocklist with {Count} domains", count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to refresh external blocklist");
            }

            try
            {
                await Task.Delay(BlocklistRefreshInterval, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void OnHeartbeat(object? sender, SiteHeartbeatEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Site)) return;
        _tracker.RecordElapsed(e.Elapsed);
    }

    private void OnAutoBlockTriggered(object? sender, AutoBlockTriggeredEventArgs e) =>
        _logger.LogInformation(
            "Auto-blocked {Site} after {Visits} visits / {Minutes:F0} min today, expires {ExpiresUtc}",
            e.Site, e.Visits, e.Seconds / 60, e.ExpiresUtc);

    private void OnDayRolledOver(object? sender, DayRolledOverEventArgs e)
    {
        _history.AppendDay(e.CompletedDayLocal, e.Sites);
        _friction.ResetForNewDay();
        _logger.LogInformation("Day rolled over: recorded usage history for {Date:yyyy-MM-dd}", e.CompletedDayLocal);
    }

    private void OnNudgeRequested(object? sender, NudgeRequestedEventArgs e) =>
        _logger.LogInformation("Friction nudge [{Phase}]: {Message}", e.Phase, e.Message);

    private void OnHardBlockTriggered(object? sender, EventArgs e) =>
        _logger.LogWarning("Hard block threshold reached (60 minutes of browsing today)");

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _monitor.Stop();
        _proxy.Stop();
        await base.StopAsync(cancellationToken);
    }
}
