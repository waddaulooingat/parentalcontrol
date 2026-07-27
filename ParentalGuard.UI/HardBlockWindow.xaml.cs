using System.ComponentModel;
using System.Windows;
using ParentalGuard.Core;

namespace ParentalGuard.UI;

/// <summary>Full-screen block shown once <see cref="FrictionEngine.HardBlockTriggered"/> fires at 60 minutes.</summary>
public partial class HardBlockWindow : Window
{
    private readonly PinStore _pinStore;
    private readonly FrictionEngine _friction;
    private bool _authorizedClose;

    public HardBlockWindow(PinStore pinStore, FrictionEngine friction)
    {
        InitializeComponent();
        _pinStore = pinStore;
        _friction = friction;
    }

    private void Override_Click(object sender, RoutedEventArgs e)
    {
        if (!PinDialog.TryUnlock(this, _pinStore)) return;

        var durationDialog = new OverrideDurationDialog { Owner = this };
        if (durationDialog.ShowDialog() != true || durationDialog.SelectedDuration is not { } duration) return;

        _friction.GrantOverride(duration);
        _authorizedClose = true;
        Close();
    }

    /// <summary>Called when an override was granted from elsewhere (e.g. the Settings tab) while this window is open.</summary>
    public void GrantedExternally()
    {
        _authorizedClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_authorizedClose) e.Cancel = true;
        base.OnClosing(e);
    }
}
