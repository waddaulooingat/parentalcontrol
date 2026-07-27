using System.Windows;
using System.Windows.Threading;

namespace ParentalGuard.UI;

/// <summary>Toast-style notification shown when the 30/30 auto-block rule fires for a site.</summary>
public partial class AutoBlockNotifyWindow : Window
{
    public AutoBlockNotifyWindow(string message)
    {
        InitializeComponent();

        MessageText.Text = message;

        Left = SystemParameters.WorkArea.Right - Width - 20;
        Top = SystemParameters.WorkArea.Bottom - Height - 20;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Close();
        };
        timer.Start();
    }

    public static AutoBlockNotifyWindow ForAutoBlock(string site, int visits) =>
        new($"You have visited {site} {visits} times today. Blocked for today.");

    private void Dismiss_Click(object sender, RoutedEventArgs e) => Close();
}
