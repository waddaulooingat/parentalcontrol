using System.Windows;

namespace ParentalGuard.UI;

/// <summary>Lets a parent (already PIN-verified) pick how long to suspend the friction ladder for.</summary>
public partial class OverrideDurationDialog : Window
{
    public TimeSpan? SelectedDuration { get; private set; }

    public OverrideDurationDialog()
    {
        InitializeComponent();
    }

    private void ThirtyMinutes_Click(object sender, RoutedEventArgs e) => Accept(TimeSpan.FromMinutes(30));

    private void OneHour_Click(object sender, RoutedEventArgs e) => Accept(TimeSpan.FromHours(1));

    private void TwoHours_Click(object sender, RoutedEventArgs e) => Accept(TimeSpan.FromHours(2));

    private void Accept(TimeSpan duration)
    {
        SelectedDuration = duration;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
