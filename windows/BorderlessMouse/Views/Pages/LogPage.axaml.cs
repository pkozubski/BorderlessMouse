using Avalonia.Controls;

namespace BorderlessMouse.Views.Pages;

public partial class LogPage : UserControl
{
    public LogPage()
    {
        InitializeComponent();
        Localization.L10n.Apply(this);
    }
}
