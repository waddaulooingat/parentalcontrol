using System.Windows;
using ParentalGuard.Core;

namespace ParentalGuard.UI;

public partial class App : Application
{
    /// <summary>
    /// Shared Core instances, one per process. ParentalGuard.UI is the sole writer of
    /// usage.json (via WindowMonitor below) and weekly_history.json - confirmed via
    /// ParentalGuard.SessionZeroProbe that a Windows Service (Session 0) cannot see the
    /// interactive user's foreground window, so tracking can only happen here, in the
    /// interactive session. ParentalGuard.Service only reads blocklist.json (reloading it
    /// periodically) to enforce whatever auto-blocks this process adds.
    /// </summary>
    public static BlocklistManager Blocklist { get; private set; } = null!;
    public static EducationalWhitelist Whitelist { get; private set; } = null!;
    public static Logger RequestLogger { get; private set; } = null!;
    public static UsageTracker Tracker { get; private set; } = null!;
    public static FrictionEngine Friction { get; private set; } = null!;
    public static WeeklyHistoryStore History { get; private set; } = null!;
    public static PinStore Pin { get; private set; } = null!;

    private static WindowMonitor? _monitor;

    protected override void OnStartup(StartupEventArgs e)
    {
        AppPaths.EnsureDirectoriesExist();

        Blocklist = new BlocklistManager();
        Whitelist = new EducationalWhitelist();
        RequestLogger = new Logger();
        Tracker = new UsageTracker(Whitelist, Blocklist);
        Friction = new FrictionEngine();
        History = new WeeklyHistoryStore();
        Pin = new PinStore();

        _monitor = new WindowMonitor();
        _monitor.SiteChanged += Tracker.OnSiteChanged;
        _monitor.Heartbeat += OnHeartbeat;
        Tracker.DayRolledOver += History.OnDayRolledOver;
        Tracker.DayRolledOver += (_, _) => Friction.ResetForNewDay();
        _monitor.Start();

        base.OnStartup(e);
    }

    private static void OnHeartbeat(object? sender, SiteHeartbeatEventArgs e)
    {
        Tracker.ResetIfNewDay();

        if (!string.IsNullOrEmpty(e.Site))
        {
            Tracker.RecordElapsed(e.Elapsed);
        }

        Friction.Evaluate(Tracker.TotalBrowserSeconds);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _monitor?.Stop();
        base.OnExit(e);
    }
}
