using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ParentalGuard.Core;

public class SiteChangedEventArgs : EventArgs
{
    public string? PreviousSite { get; init; }
    public string? Site { get; init; }
}

public class SiteHeartbeatEventArgs : EventArgs
{
    public string? Site { get; init; }
    public TimeSpan Elapsed { get; init; }
}

/// <summary>
/// Polls the foreground window on a timer, recognizes known browser processes,
/// and heuristically extracts the site being browsed from the window title.
/// </summary>
public partial class WindowMonitor : IDisposable
{
    private static readonly HashSet<string> BrowserProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "iexplore", "vivaldi",
    };

    // Common social/streaming sites whose window titles rarely include the raw domain.
    private static readonly (string Keyword, string Domain)[] KnownSiteKeywords =
    {
        ("youtube", "youtube.com"),
        ("reddit", "reddit.com"),
        ("facebook", "facebook.com"),
        ("instagram", "instagram.com"),
        ("tiktok", "tiktok.com"),
        ("twitch", "twitch.tv"),
        ("discord", "discord.com"),
        ("pinterest", "pinterest.com"),
        ("snapchat", "snapchat.com"),
        ("netflix", "netflix.com"),
        ("amazon", "amazon.com"),
        ("twitter", "twitter.com"),
        (" x ", "twitter.com"),
        ("hulu", "hulu.com"),
        ("disney+", "disneyplus.com"),
        ("roblox", "roblox.com"),
        ("steam", "store.steampowered.com"),
    };

    [GeneratedRegex(@"([a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,}", RegexOptions.IgnoreCase)]
    private static partial Regex DomainPattern();

    private static readonly string[] BrowserTitleSuffixes =
    {
        " - Google Chrome", " - Mozilla Firefox", " - Microsoft​ Edge", " - Microsoft Edge",
        " - Brave", " - Opera", " and 1 more page - Personal - Microsoft​ Edge", " - Vivaldi",
    };

    private readonly System.Timers.Timer _timer;
    private string? _lastSite;
    private DateTime _lastTick = DateTime.UtcNow;
    private bool _disposed;

    public event EventHandler<SiteChangedEventArgs>? SiteChanged;
    public event EventHandler<SiteHeartbeatEventArgs>? Heartbeat;

    public WindowMonitor(TimeSpan? pollInterval = null)
    {
        _timer = new System.Timers.Timer((pollInterval ?? TimeSpan.FromSeconds(2)).TotalMilliseconds)
        {
            AutoReset = true,
        };
        _timer.Elapsed += (_, _) => Poll();
    }

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    private void Poll()
    {
        var now = DateTime.UtcNow;
        var elapsed = now - _lastTick;
        _lastTick = now;

        var site = TryGetActiveBrowserSite();

        if (!string.Equals(site, _lastSite, StringComparison.OrdinalIgnoreCase))
        {
            var previous = _lastSite;
            _lastSite = site;
            SiteChanged?.Invoke(this, new SiteChangedEventArgs { PreviousSite = previous, Site = site });
        }

        Heartbeat?.Invoke(this, new SiteHeartbeatEventArgs { Site = site, Elapsed = elapsed });
    }

    /// <summary>Returns the best-guess site domain for the current foreground browser window, or null.</summary>
    public static string? TryGetActiveBrowserSite()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;

        _ = GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return null;

        string processName;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            processName = process.ProcessName;
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (!BrowserProcessNames.Contains(processName)) return null;

        var title = GetWindowTitle(hwnd);
        if (string.IsNullOrWhiteSpace(title)) return null;

        return ExtractSiteFromTitle(title);
    }

    internal static string? ExtractSiteFromTitle(string title)
    {
        var trimmed = title;
        foreach (var suffix in BrowserTitleSuffixes)
        {
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[..^suffix.Length];
                break;
            }
        }

        var domainMatch = DomainPattern().Match(trimmed);
        if (domainMatch.Success)
        {
            return domainMatch.Value.ToLowerInvariant();
        }

        var lowerTitle = trimmed.ToLowerInvariant();
        foreach (var (keyword, domain) in KnownSiteKeywords)
        {
            if (lowerTitle.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return domain;
            }
        }

        return null;
    }

    private static string? GetWindowTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0) return null;

        var builder = new StringBuilder(length + 1);
        GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
