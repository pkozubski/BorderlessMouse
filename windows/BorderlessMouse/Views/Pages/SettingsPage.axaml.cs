using Avalonia.Controls;

namespace BorderlessMouse.Views.Pages;

public partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
        Localization.L10n.Apply(this);
    }
}
