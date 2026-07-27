using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ParentalGuard.Core;

namespace ParentalGuard.SessionZeroProbe;

/// <summary>
/// One-shot diagnostic: when installed and run as a real Windows Service, this logs whether
/// the service (Session 0) can see the interactive user's foreground window at all, and
/// whether WindowMonitor's production logic (which assumes it can) actually detects a browser.
/// Runs for a fixed window then stops itself; this is throwaway test tooling, not a shipped
/// component of ParentalGuard.
/// </summary>
public class Probe : BackgroundService
{
    private const int TickCount = 20;
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);

    private readonly ILogger<Probe> _logger;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly string _logPath;

    public Probe(ILogger<Probe> logger, IHostApplicationLifetime lifetime)
    {
        _logger = logger;
        _lifetime = lifetime;
        _logPath = Environment.GetEnvironmentVariable("PROBE_LOG_PATH")
                   ?? @"C:\ParentalGuardTest\probe.log";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);

        WriteLine($"=== Probe started {DateTime.Now:O} ===");
        WriteLine($"UserInteractive={Environment.UserInteractive}");
        WriteLine($"CurrentProcess.SessionId={Process.GetCurrentProcess().SessionId}");
        WriteLine($"WTSGetActiveConsoleSessionId={WTSGetActiveConsoleSessionId()}");

        for (var i = 0; i < TickCount && !stoppingToken.IsCancellationRequested; i++)
        {
            RunOneTick(i);

            try
            {
                await Task.Delay(TickInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        WriteLine($"=== Probe finished {DateTime.Now:O} ===");
        _lifetime.StopApplication();
    }

    private void RunOneTick(int tick)
    {
        var hwnd = GetForegroundWindow();
        var rawTitle = GetWindowTitle(hwnd);

        string processInfo = "(none)";
        if (hwnd != IntPtr.Zero)
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != 0)
            {
                try
                {
                    using var process = Process.GetProcessById((int)pid);
                    processInfo = $"{process.ProcessName} (pid {pid}, session {process.SessionId})";
                }
                catch (ArgumentException)
                {
                    processInfo = $"(pid {pid}, process exited or inaccessible)";
                }
            }
        }

        var detectedSite = WindowMonitor.TryGetActiveBrowserSite();

        WriteLine(
            $"[tick {tick}] hwnd=0x{hwnd:X} title=\"{rawTitle}\" " +
            $"foregroundProcess={processInfo} WindowMonitor.TryGetActiveBrowserSite()={detectedSite ?? "(null)"}");

        _logger.LogInformation(
            "tick {Tick}: hwnd={Hwnd} title={Title} process={Process} detectedSite={Site}",
            tick, hwnd, rawTitle, processInfo, detectedSite);
    }

    private void WriteLine(string line)
    {
        File.AppendAllText(_logPath, line + Environment.NewLine);
    }

    private static string? GetWindowTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0) return null;

        var builder = new StringBuilder(length + 1);
        GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();
}
