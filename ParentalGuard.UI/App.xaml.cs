using System.Windows;
using ParentalGuard.Core;

namespace ParentalGuard.UI;

public partial class App : Application
{
    /// <summary>
    /// Shared Core instances, one per process. The Windows Service is the single writer of
    /// usage.json/blocklist.json; the UI reloads them from disk to stay in sync (see
    /// UsageTracker.Reload/BlocklistManager.Reload) rather than tracking independently.
    /// </summary>
    public static BlocklistManager Blocklist { get; private set; } = null!;
    public static EducationalWhitelist Whitelist { get; private set; } = null!;
    public static Logger RequestLogger { get; private set; } = null!;
    public static UsageTracker Tracker { get; private set; } = null!;
    public static FrictionEngine Friction { get; private set; } = null!;
    public static WeeklyHistoryStore History { get; private set; } = null!;
    public static PinStore Pin { get; private set; } = null!;

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

        base.OnStartup(e);
    }
}
