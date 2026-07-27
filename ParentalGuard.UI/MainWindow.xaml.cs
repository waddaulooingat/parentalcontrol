using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ParentalGuard.Core;

namespace ParentalGuard.UI;

public partial class MainWindow : Window
{
    private record ActivityLogRow(string Time, string Host, string Url, string Status);

    private record ScreenTimeRow(string Site, int Visits, string Time);

    private readonly DispatcherTimer _refreshTimer;
    private HardBlockWindow? _hardBlockWindow;

    public MainWindow()
    {
        InitializeComponent();

        App.Friction.NudgeRequested += OnNudgeRequested;
        App.Friction.HardBlockTriggered += OnHardBlockTriggered;
        App.Tracker.AutoBlockTriggered += OnAutoBlockTriggered;

        RefreshAll();

        // WindowMonitor (owned by App) writes usage.json/blocklist.json on its own timer thread;
        // this timer just keeps the visible tabs in sync with that live, in-process state.
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += (_, _) => RefreshAll();
        _refreshTimer.Start();
    }

    private void RefreshAll()
    {
        RefreshActivityLog();
        RefreshScreenTime();
        RefreshDomains();
        RefreshKeywords();
        RefreshWhitelist();
    }

    private void RefreshActivityLog()
    {
        var rows = App.RequestLogger.GetRecent(200)
            .Select(r => new ActivityLogRow(
                r.TimestampUtc.ToLocalTime().ToString("g"), r.Host, r.Url, r.Blocked ? "Blocked" : "Allowed"))
            .ToList();
        ActivityLogGrid.ItemsSource = rows;
    }

    private void RefreshScreenTime()
    {
        var snapshot = App.Tracker.TodaySnapshot;
        var rows = snapshot
            .OrderByDescending(kv => kv.Value.Seconds)
            .Select(kv => new ScreenTimeRow(kv.Key, kv.Value.Visits, FormatDuration(kv.Value.Seconds)))
            .ToList();
        ScreenTimeGrid.ItemsSource = rows;

        TotalTimeText.Text = $"Total today: {FormatDuration(App.Tracker.TotalBrowserSeconds)}";
        PhaseText.Text = App.Friction.IsOverrideActive
            ? $"Override active - {FormatDuration(App.Friction.OverrideRemaining.TotalSeconds)} remaining"
            : $"Phase: {App.Friction.CurrentPhase}";
    }

    private void RefreshDomains()
    {
        var selected = DomainsList.SelectedItem as string;
        DomainsList.ItemsSource = App.Blocklist.Domains.OrderBy(d => d).ToList();
        DomainsList.SelectedItem = selected;
        ExternalDomainCountText.Text = $"{App.Blocklist.ExternalDomainCount} auto-fetched domains loaded";
    }

    private void RefreshKeywords()
    {
        var selected = KeywordsList.SelectedItem as string;
        KeywordsList.ItemsSource = App.Blocklist.Keywords.OrderBy(k => k).ToList();
        KeywordsList.SelectedItem = selected;
    }

    private void RefreshWhitelist()
    {
        BuiltInWhitelistList.ItemsSource = App.Whitelist.BuiltInSites.OrderBy(s => s).ToList();
        var selected = CustomWhitelistList.SelectedItem as string;
        CustomWhitelistList.ItemsSource = App.Whitelist.CustomSites.OrderBy(s => s).ToList();
        CustomWhitelistList.SelectedItem = selected;
    }

    private static string FormatDuration(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalHours}h {ts.Minutes}m";
    }

    private void OnAutoBlockTriggered(object? sender, AutoBlockTriggeredEventArgs e) =>
        Dispatcher.Invoke(() => AutoBlockNotifyWindow.ForAutoBlock(e.Site, e.Visits).Show());

    private void OnNudgeRequested(object? sender, NudgeRequestedEventArgs e) =>
        Dispatcher.Invoke(() => new AutoBlockNotifyWindow(e.Message).Show());

    private void OnHardBlockTriggered(object? sender, EventArgs e) =>
        Dispatcher.Invoke(() =>
        {
            if (_hardBlockWindow is { IsVisible: true }) return;

            _hardBlockWindow = new HardBlockWindow(App.Pin, App.Friction);
            _hardBlockWindow.Closed += (_, _) => _hardBlockWindow = null;
            _hardBlockWindow.Show();
        });

    private void RefreshActivityLog_Click(object sender, RoutedEventArgs e) => RefreshActivityLog();

    private void RefreshScreenTime_Click(object sender, RoutedEventArgs e) => RefreshScreenTime();

    private void AddDomain_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NewDomainBox.Text)) return;
        App.Blocklist.AddDomain(NewDomainBox.Text);
        NewDomainBox.Clear();
        RefreshDomains();
    }

    private void RemoveDomain_Click(object sender, RoutedEventArgs e)
    {
        if (DomainsList.SelectedItem is not string domain) return;
        App.Blocklist.RemoveDomain(domain);
        RefreshDomains();
    }

    private void AddKeyword_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NewKeywordBox.Text)) return;
        App.Blocklist.AddKeyword(NewKeywordBox.Text);
        NewKeywordBox.Clear();
        RefreshKeywords();
    }

    private void RemoveKeyword_Click(object sender, RoutedEventArgs e)
    {
        if (KeywordsList.SelectedItem is not string keyword) return;
        App.Blocklist.RemoveKeyword(keyword);
        RefreshKeywords();
    }

    private void AddWhitelist_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NewWhitelistBox.Text)) return;
        App.Whitelist.Add(NewWhitelistBox.Text);
        NewWhitelistBox.Clear();
        RefreshWhitelist();
    }

    private void RemoveWhitelist_Click(object sender, RoutedEventArgs e)
    {
        if (CustomWhitelistList.SelectedItem is not string site) return;
        App.Whitelist.Remove(site);
        RefreshWhitelist();
    }

    private void GenerateWeeklyReport_Click(object sender, RoutedEventArgs e)
    {
        if (!PinDialog.TryUnlock(this, App.Pin)) return;

        var days = App.History.GetLastDays(7);
        var path = WeeklyReportGenerator.Generate(days);
        MessageBox.Show(this, $"Weekly report saved to:\n{path}", "Parental Guard", MessageBoxButton.OK, MessageBoxImage.Information);

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
        }
    }

    private void GrantOverride_Click(object sender, RoutedEventArgs e)
    {
        if (!PinDialog.TryUnlock(this, App.Pin)) return;

        var durationDialog = new OverrideDurationDialog { Owner = this };
        if (durationDialog.ShowDialog() != true || durationDialog.SelectedDuration is not { } duration) return;

        App.Friction.GrantOverride(duration);
        RefreshScreenTime();

        if (_hardBlockWindow is { IsVisible: true } window)
        {
            window.GrantedExternally();
        }
    }

    private void ChangePin_Click(object sender, RoutedEventArgs e)
    {
        if (App.Pin.HasPin && !PinDialog.TryUnlock(this, App.Pin)) return;

        var setup = new PinDialog("Enter new parent PIN") { Owner = this };
        if (setup.ShowDialog() == true && !string.IsNullOrWhiteSpace(setup.EnteredPin))
        {
            App.Pin.SetPin(setup.EnteredPin);
            MessageBox.Show(this, "PIN updated.", "Parental Guard", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void RefreshExternalBlocklist_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        button.IsEnabled = false;
        try
        {
            var fetcher = new BlocklistFetcher();
            var count = await fetcher.RefreshAsync(App.Blocklist);
            RefreshDomains();
            MessageBox.Show(this, $"Loaded {count} auto-blocked domains.", "Parental Guard", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            MessageBox.Show(this, "Could not refresh the external blocklist. Check your internet connection.", "Parental Guard", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void InstallService_Click(object sender, RoutedEventArgs e) => RunElevatedScCommand(
        $"create ParentalGuard binPath= \"{ServiceExecutablePath}\" start= auto DisplayName= \"Parental Guard\"");

    private void StartService_Click(object sender, RoutedEventArgs e) => RunElevatedScCommand("start ParentalGuard");

    private void StopService_Click(object sender, RoutedEventArgs e) => RunElevatedScCommand("stop ParentalGuard");

    private void UninstallService_Click(object sender, RoutedEventArgs e) => RunElevatedScCommand("delete ParentalGuard");

    private static string ServiceExecutablePath =>
        Path.Combine(AppContext.BaseDirectory, "ParentalGuard.Service.exe");

    private void RunElevatedScCommand(string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo("sc.exe", arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
            });
        }
        catch (Win32Exception)
        {
            // User declined the UAC prompt.
        }
    }
}
