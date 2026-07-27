using System.Windows;
using ParentalGuard.Core;

namespace ParentalGuard.UI;

public partial class PinDialog : Window
{
    public string EnteredPin { get; private set; } = string.Empty;

    public PinDialog(string message = "Enter parent PIN")
    {
        InitializeComponent();
        MessageText.Text = message;
        Loaded += (_, _) => PinBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        EnteredPin = PinBox.Password;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    /// <summary>
    /// Prompts for the parent PIN (or, if none is set yet, prompts to create one).
    /// Returns true once the correct PIN has been entered.
    /// </summary>
    public static bool TryUnlock(Window? owner, PinStore pinStore)
    {
        if (!pinStore.HasPin)
        {
            var setup = new PinDialog("Create a parent PIN") { Owner = owner };
            if (setup.ShowDialog() == true && !string.IsNullOrWhiteSpace(setup.EnteredPin))
            {
                pinStore.SetPin(setup.EnteredPin);
                return true;
            }

            return false;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var dialog = new PinDialog("Enter parent PIN") { Owner = owner };
            if (dialog.ShowDialog() != true) return false;

            if (pinStore.Verify(dialog.EnteredPin)) return true;

            MessageBox.Show(owner, "Incorrect PIN.", "Parental Guard", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        return false;
    }
}
