using Newtonsoft.Json;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace ParentalGuard.Core;

public class DayUsageRecord
{
    public DateTime DateLocal { get; set; }
    public Dictionary<string, SiteUsage> Sites { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Persists a rolling 35-day history of daily site usage to weekly_history.json.</summary>
public class WeeklyHistoryStore
{
    private const int RetentionDays = 35;

    private readonly string _path;
    private readonly object _lock = new();
    private List<DayUsageRecord> _days = new();

    public WeeklyHistoryStore(string? historyPath = null)
    {
        _path = historyPath ?? Path.Combine(AppPaths.DataDirectory, "weekly_history.json");
        Load();
    }

    /// <summary>Wire this to <see cref="UsageTracker.DayRolledOver"/> to record each finished day.</summary>
    public void OnDayRolledOver(object? sender, DayRolledOverEventArgs e) => AppendDay(e.CompletedDayLocal, e.Sites);

    public void AppendDay(DateTime dayLocal, IReadOnlyDictionary<string, SiteUsage> sites)
    {
        lock (_lock)
        {
            _days.RemoveAll(d => d.DateLocal.Date == dayLocal.Date);
            _days.Add(new DayUsageRecord
            {
                DateLocal = dayLocal.Date,
                Sites = sites.ToDictionary(kv => kv.Key, kv => new SiteUsage { Visits = kv.Value.Visits, Seconds = kv.Value.Seconds }, StringComparer.OrdinalIgnoreCase),
            });

            var cutoff = DateTime.Today.AddDays(-RetentionDays);
            _days.RemoveAll(d => d.DateLocal < cutoff);
            _days = _days.OrderBy(d => d.DateLocal).ToList();
        }

        Save();
    }

    public IReadOnlyList<DayUsageRecord> GetLastDays(int count)
    {
        lock (_lock)
        {
            return _days.OrderByDescending(d => d.DateLocal).Take(count).OrderBy(d => d.DateLocal).ToList();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path);
            var days = JsonConvert.DeserializeObject<List<DayUsageRecord>>(json);
            if (days != null)
            {
                lock (_lock) { _days = days; }
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
        List<DayUsageRecord> days;
        lock (_lock) { days = _days; }

        Directory.CreateDirectory(AppPaths.DataDirectory);
        var json = JsonConvert.SerializeObject(days, Formatting.Indented);
        File.WriteAllText(_path, json);
    }
}

/// <summary>Strips adult/gambling domains out of usage data before it's shown to a parent.</summary>
public static class ContentFilter
{
    private static readonly string[] AdultGamblingKeywords =
    {
        "porn", "xxx", "xvideos", "xnxx", "pornhub", "onlyfans", "cam4", "redtube", "xhamster",
        "casino", "poker", "bet365", "bovada", "draftkings", "fanduel", "gambling", "slots",
    };

    public static bool IsAdultOrGambling(string domain) =>
        AdultGamblingKeywords.Any(k => domain.Contains(k, StringComparison.OrdinalIgnoreCase));

    public static Dictionary<string, SiteUsage> Filter(IReadOnlyDictionary<string, SiteUsage> sites) =>
        sites.Where(kv => !IsAdultOrGambling(kv.Key))
             .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Generates the parent-facing weekly usage PDF report via PdfSharp.</summary>
public static class WeeklyReportGenerator
{
    public static string Generate(IReadOnlyList<DayUsageRecord> days, string? outputDirectory = null)
    {
        var dir = outputDirectory ?? AppPaths.ReportsDirectory;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"WeeklyReport_{DateTime.Today:yyyy-MM-dd}.pdf");

        var filteredDays = days
            .Select(d => new DayUsageRecord { DateLocal = d.DateLocal, Sites = ContentFilter.Filter(d.Sites) })
            .OrderBy(d => d.DateLocal)
            .ToList();

        var siteTotals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var day in filteredDays)
        {
            foreach (var (site, usage) in day.Sites)
            {
                siteTotals[site] = siteTotals.GetValueOrDefault(site) + usage.Seconds;
            }
        }

        var totalSeconds = siteTotals.Values.Sum();
        var topSite = siteTotals.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault() ?? "(none)";

        var autoBlockedSites = filteredDays
            .SelectMany(d => d.Sites.Where(kv =>
                kv.Value.Visits >= UsageTracker.AutoBlockVisitThreshold ||
                kv.Value.Seconds >= UsageTracker.AutoBlockTimeThreshold.TotalSeconds)
                .Select(kv => kv.Key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        using var document = new PdfDocument();
        document.Info.Title = "Parental Guard Weekly Report";

        var page = document.AddPage();
        using var gfx = XGraphics.FromPdfPage(page);

        var titleFont = new XFont("Verdana", 20, XFontStyleEx.Bold);
        var headerFont = new XFont("Verdana", 13, XFontStyleEx.Bold);
        var bodyFont = new XFont("Verdana", 11, XFontStyleEx.Regular);

        double y = 40;
        gfx.DrawString("Parental Guard - Weekly Report", titleFont, XBrushes.Black, new XPoint(40, y));
        y += 34;

        gfx.DrawString($"Total time: {FormatDuration(totalSeconds)}", bodyFont, XBrushes.Black, new XPoint(40, y));
        y += 20;
        gfx.DrawString($"Top site: {topSite}", bodyFont, XBrushes.Black, new XPoint(40, y));
        y += 20;
        gfx.DrawString(
            "Auto-blocked sites: " + (autoBlockedSites.Count == 0 ? "none" : string.Join(", ", autoBlockedSites)),
            bodyFont, XBrushes.Black, new XPoint(40, y));
        y += 34;

        gfx.DrawString("Daily breakdown", headerFont, XBrushes.Black, new XPoint(40, y));
        y += 24;

        foreach (var day in filteredDays)
        {
            var daySeconds = day.Sites.Values.Sum(s => s.Seconds);
            var dayVisits = day.Sites.Values.Sum(s => s.Visits);
            gfx.DrawString(
                $"{day.DateLocal:ddd MMM d}: {FormatDuration(daySeconds)}, {dayVisits} visits",
                bodyFont, XBrushes.Black, new XPoint(50, y));
            y += 18;
        }

        document.Save(path);
        return path;
    }

    private static string FormatDuration(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalHours}h {ts.Minutes}m";
    }
}
