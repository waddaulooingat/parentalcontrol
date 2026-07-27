using ParentalGuard.Core;

namespace ParentalGuard.Service;

/// <summary>
/// Hosts the local blocking proxy. Runs a watchdog every 15s, reloads blocklist.json every
/// 10s (so auto-blocks written by ParentalGuard.UI reach the proxy promptly), and refreshes
/// the external (StevenBlack) blocklist 5 minutes after startup and every 24h thereafter.
/// </summary>
/// <remarks>
/// This service does NOT run WindowMonitor/UsageTracker/FrictionEngine. A Windows Service
/// runs in Session 0, isolated from the interactive desktop - confirmed empirically via
/// ParentalGuard.SessionZeroProbe on a real windows-latest runner: GetForegroundWindow
/// returned a null handle on all 20/20 polls while a real browser window was open and
/// active in the interactive session the whole time. The proxy is unaffected (it operates
/// purely at the network layer, no desktop access needed). Tracking, friction nudges, the
/// hard-block window, and auto-block notifications all live in ParentalGuard.UI instead,
/// which runs in the interactive session and is the sole writer of usage.json going forward.
/// This service only reads blocklist.json (to enforce auto-blocks the UI adds) and writes
/// requests.db (proxy request log) - see claude_handover.md for the full writeup.
/// </remarks>
public class ParentalGuardService : BackgroundService
{
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan BlocklistReloadInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan InitialBlocklistRefreshDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan BlocklistRefreshInterval = TimeSpan.FromHours(24);

    private readonly ILogger<ParentalGuardService> _logger;

    private readonly BlocklistManager _blocklist;
    private readonly Core.Logger _requestLogger;
    private readonly ProxyEngine _proxy;
    private readonly BlocklistFetcher _blocklistFetcher;

    public ParentalGuardService(ILogger<ParentalGuardService> logger)
    {
        _logger = logger;

        AppPaths.EnsureDirectoriesExist();

        _blocklist = new BlocklistManager();
        _requestLogger = new Core.Logger();
        _proxy = new ProxyEngine(_blocklist, _requestLogger);
        _blocklistFetcher = new BlocklistFetcher();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _proxy.Start();
        _logger.LogInformation("Proxy engine listening on 127.0.0.1:8877");

        var refreshLoop = RefreshBlocklistLoopAsync(stoppingToken);

        using var watchdogTimer = new PeriodicTimer(WatchdogInterval);
        using var reloadTimer = new PeriodicTimer(BlocklistReloadInterval);

        var watchdogLoop = WatchdogLoopAsync(watchdogTimer, stoppingToken);
        var reloadLoop = BlocklistReloadLoopAsync(reloadTimer, stoppingToken);

        await Task.WhenAll(watchdogLoop, reloadLoop, refreshLoop);
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

    private async Task BlocklistReloadLoopAsync(PeriodicTimer timer, CancellationToken token)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                _blocklist.Reload();
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

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _proxy.Stop();
        await base.StopAsync(cancellationToken);
    }
}
