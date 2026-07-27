using System.ComponentModel;
using System.Windows;
using ParentalGuard.Core;

namespace ParentalGuard.UI;

/// <summary>Full-screen block shown once <see cref="FrictionEngine.HardBlockTriggered"/> fires at 60 minutes.</summary>
public partial class HardBlockWindow : Window
{
    private readonly PinStore _pinStore;
    private bool _authorizedClose;

    public HardBlockWindow(PinStore pinStore)
    {
        InitializeComponent();
        _pinStore = pinStore;
    }

    private void Override_Click(object sender, RoutedEventArgs e)
    {
        if (!PinDialog.TryUnlock(this, _pinStore)) return;

        _authorizedClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_authorizedClose) e.Cancel = true;
        base.OnClosing(e);
    }
}
