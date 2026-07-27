using System.Net;
using System.Text;
using Newtonsoft.Json;

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

/// <summary>Generates the parent-facing weekly usage report as a self-contained HTML file.</summary>
public static class WeeklyReportGenerator
{
    public static string Generate(IReadOnlyList<DayUsageRecord> days, string? outputDirectory = null)
    {
        var dir = outputDirectory ?? AppPaths.ReportsDirectory;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"WeeklyReport_{DateTime.Today:yyyy-MM-dd}.html");

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

        var html = BuildHtml(filteredDays, totalSeconds, topSite, autoBlockedSites);
        File.WriteAllText(path, html);
        return path;
    }

    private static string BuildHtml(
        List<DayUsageRecord> filteredDays, double totalSeconds, string topSite, List<string> autoBlockedSites)
    {
        var rows = new StringBuilder();
        foreach (var day in filteredDays)
        {
            var daySeconds = day.Sites.Values.Sum(s => s.Seconds);
            var dayVisits = day.Sites.Values.Sum(s => s.Visits);
            rows.Append("<tr><td>").Append(WebUtility.HtmlEncode(day.DateLocal.ToString("ddd MMM d")))
                .Append("</td><td>").Append(FormatDuration(daySeconds))
                .Append("</td><td>").Append(dayVisits)
                .Append("</td></tr>\n");
        }

        var autoBlockedText = autoBlockedSites.Count == 0
            ? "none"
            : WebUtility.HtmlEncode(string.Join(", ", autoBlockedSites));

        return $@"
            <!doctype html>
            <html lang=""en"">
            <head>
            <meta charset=""utf-8"">
            <title>Parental Guard - Weekly Report</title>
            <style>
              body {{ font-family: Verdana, Arial, sans-serif; color: #1a1a1a; margin: 40px; }}
              h1 {{ font-size: 24px; margin-bottom: 24px; }}
              h2 {{ font-size: 16px; margin-top: 32px; }}
              .summary p {{ margin: 6px 0; }}
              table {{ border-collapse: collapse; margin-top: 12px; }}
              th, td {{ padding: 6px 16px 6px 0; text-align: left; }}
              th {{ border-bottom: 1px solid #999; }}
            </style>
            </head>
            <body>
            <h1>Parental Guard - Weekly Report</h1>
            <div class=""summary"">
              <p><strong>Total time:</strong> {FormatDuration(totalSeconds)}</p>
              <p><strong>Top site:</strong> {WebUtility.HtmlEncode(topSite)}</p>
              <p><strong>Auto-blocked sites:</strong> {autoBlockedText}</p>
            </div>
            <h2>Daily breakdown</h2>
            <table>
              <tr><th>Day</th><th>Time</th><th>Visits</th></tr>
              {rows}
            </table>
            </body>
            </html>
            ";
    }

    private static string FormatDuration(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalHours}h {ts.Minutes}m";
    }
}
