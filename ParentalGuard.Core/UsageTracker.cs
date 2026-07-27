using Newtonsoft.Json;

namespace ParentalGuard.Core;

public class SiteUsage
{
    public int Visits { get; set; }
    public double Seconds { get; set; }
}

public class AutoBlockTriggeredEventArgs : EventArgs
{
    public string Site { get; init; } = string.Empty;
    public int Visits { get; init; }
    public double Seconds { get; init; }
    public DateTime ExpiresUtc { get; init; }
}

public class DayRolledOverEventArgs : EventArgs
{
    public DateTime CompletedDayLocal { get; init; }
    public IReadOnlyDictionary<string, SiteUsage> Sites { get; init; } = new Dictionary<string, SiteUsage>();
}

/// <summary>
/// Tracks per-site visit counts and time-on-site for the current day, enforces the
/// 30 visits / 30 minutes auto-block rule, and persists state to usage.json.
/// </summary>
public class UsageTracker
{
    public const int AutoBlockVisitThreshold = 30;
    public static readonly TimeSpan AutoBlockTimeThreshold = TimeSpan.FromMinutes(30);

    private class PersistedState
    {
        public string Date { get; set; } = string.Empty;
        public Dictionary<string, SiteUsage> Sites { get; set; } = new();
    }

    private readonly string _path;
    private readonly EducationalWhitelist _whitelist;
    private readonly BlocklistManager _blocklist;
    private readonly object _lock = new();

    private Dictionary<string, SiteUsage> _today = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _autoBlockedToday = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _currentDayLocal = DateTime.Today;
    private string? _currentSite;

    public event EventHandler<AutoBlockTriggeredEventArgs>? AutoBlockTriggered;
    public event EventHandler<DayRolledOverEventArgs>? DayRolledOver;

    public UsageTracker(EducationalWhitelist whitelist, BlocklistManager blocklist, string? usagePath = null)
    {
        _whitelist = whitelist;
        _blocklist = blocklist;
        _path = usagePath ?? Path.Combine(AppPaths.DataDirectory, "usage.json");
        Load();
    }

    /// <summary>Total seconds spent with a (non-whitelisted or whitelisted) browser in the foreground today.</summary>
    public double TotalBrowserSeconds
    {
        get { lock (_lock) { return _today.Values.Sum(s => s.Seconds); } }
    }

    public IReadOnlyDictionary<string, SiteUsage> TodaySnapshot
    {
        get
        {
            lock (_lock)
            {
                return _today.ToDictionary(kv => kv.Key, kv => new SiteUsage { Visits = kv.Value.Visits, Seconds = kv.Value.Seconds });
            }
        }
    }

    /// <summary>Re-reads usage.json from disk, picking up changes recorded by another process (e.g. the service).</summary>
    public void Reload() => Load();

    /// <summary>Call whenever <see cref="WindowMonitor"/> detects the active browser site has changed.</summary>
    public void OnSiteChanged(object? sender, SiteChangedEventArgs e)
    {
        ResetIfNewDay();

        var site = e.Site?.Trim().ToLowerInvariant();
        lock (_lock)
        {
            _currentSite = site;
            if (string.IsNullOrEmpty(site)) return;
            if (!_today.TryGetValue(site, out var usage))
            {
                usage = new SiteUsage();
                _today[site] = usage;
            }

            if (!_whitelist.IsWhitelisted(site))
            {
                usage.Visits++;
            }
        }

        Save();
        EvaluateAutoBlock(site);
    }

    /// <summary>Call on a periodic timer (e.g. every 10s) with the elapsed time while a browser was foreground.</summary>
    public void RecordElapsed(TimeSpan elapsed)
    {
        ResetIfNewDay();

        string? site;
        lock (_lock)
        {
            site = _currentSite;
            if (string.IsNullOrEmpty(site)) return;

            if (!_today.TryGetValue(site, out var usage))
            {
                usage = new SiteUsage();
                _today[site] = usage;
            }

            usage.Seconds += elapsed.TotalSeconds;
        }

        Save();
        EvaluateAutoBlock(site);
    }

    private void EvaluateAutoBlock(string? site)
    {
        if (string.IsNullOrEmpty(site)) return;
        if (_whitelist.IsWhitelisted(site)) return;

        int visits;
        double seconds;
        lock (_lock)
        {
            if (_autoBlockedToday.Contains(site)) return;
            if (!_today.TryGetValue(site, out var usage)) return;
            visits = usage.Visits;
            seconds = usage.Seconds;
        }

        if (visits < AutoBlockVisitThreshold && seconds < AutoBlockTimeThreshold.TotalSeconds) return;

        lock (_lock)
        {
            _autoBlockedToday.Add(site);
        }

        var expiresUtc = DateTime.UtcNow.Date.AddDays(1).ToUniversalTime();
        _blocklist.AddAutoBlock(site, expiresUtc);

        AutoBlockTriggered?.Invoke(this, new AutoBlockTriggeredEventArgs
        {
            Site = site,
            Visits = visits,
            Seconds = seconds,
            ExpiresUtc = expiresUtc,
        });
    }

    /// <summary>Rolls usage over to a new day, raising <see cref="DayRolledOver"/> with the finished day's data.</summary>
    public void ResetIfNewDay()
    {
        Dictionary<string, SiteUsage>? finishedDay = null;
        DateTime finishedDayDate = default;

        lock (_lock)
        {
            if (DateTime.Today == _currentDayLocal) return;

            finishedDay = _today;
            finishedDayDate = _currentDayLocal;

            _today = new Dictionary<string, SiteUsage>(StringComparer.OrdinalIgnoreCase);
            _autoBlockedToday.Clear();
            _currentDayLocal = DateTime.Today;
            _currentSite = null;
        }

        _blocklist.PurgeExpiredAutoBlocks();
        Save();

        DayRolledOver?.Invoke(this, new DayRolledOverEventArgs
        {
            CompletedDayLocal = finishedDayDate,
            Sites = finishedDay,
        });
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;

            var json = File.ReadAllText(_path);
            var state = JsonConvert.DeserializeObject<PersistedState>(json);
            if (state == null) return;

            if (!DateTime.TryParse(state.Date, out var date) || date.Date != DateTime.Today)
            {
                return;
            }

            lock (_lock)
            {
                _today = new Dictionary<string, SiteUsage>(state.Sites, StringComparer.OrdinalIgnoreCase);
                _currentDayLocal = date.Date;
            }
        }
        catch (IOException)
        {
        }
        catch (JsonException)
        {
        }
    }

    private void Save()
    {
        PersistedState state;
        lock (_lock)
        {
            state = new PersistedState
            {
                Date = _currentDayLocal.ToString("yyyy-MM-dd"),
                Sites = new Dictionary<string, SiteUsage>(_today, StringComparer.OrdinalIgnoreCase),
            };
        }

        Directory.CreateDirectory(AppPaths.DataDirectory);
        var json = JsonConvert.SerializeObject(state, Formatting.Indented);
        File.WriteAllText(_path, json);
    }
}
