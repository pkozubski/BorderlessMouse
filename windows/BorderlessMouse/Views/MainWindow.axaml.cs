using Avalonia.Controls;
using Avalonia.Media;
using BorderlessMouse.Views.Pages;
using BorderlessMouse.Localization;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Windowing;

namespace BorderlessMouse.Views;

public partial class MainWindow : AppWindow
{
    /// <summary>Kolejność stron w pasku bocznym – używana też przez tryb <c>--screenshot</c>.</summary>
    public static readonly IReadOnlyList<string> PageTags = new[] { "connection", "control", "settings", "log" };

    private readonly Dictionary<string, Control> _pages = new();

    public MainWindow()
    {
        InitializeComponent();
        L10n.Apply(this);
        foreach (var item in Nav.MenuItems.OfType<NavigationViewItem>()
                     .Concat(Nav.FooterMenuItems.OfType<NavigationViewItem>()))
        {
            if (item.Content is string label) item.Content = L10n.TranslateText(label);
        }
        TitleBar.ExtendsContentIntoTitleBar = true;
        TitleBar.TitleBarHitTestType = TitleBarHitTestType.Complex;

        // Mica na Windows 11; gdzie niedostępna – jednolite tło jak w Ustawieniach
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Mica, WindowTransparencyLevel.None };
        Opened += (_, _) => ApplyBackdrop();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == ActualTransparencyLevelProperty) ApplyBackdrop();
        };

        Closing += (_, e) =>
        {
            // zamknięcie okna = schowanie do zasobnika; wyjście przez menu ikony
            if (App.Current is App app && !app.IsExiting)
            {
                e.Cancel = true;
                Hide();
            }
        };

        NavigateTo(PageTags[0]);
    }

    /// <summary>Wybiera stronę w pasku bocznym po jej znaczniku.</summary>
    public void NavigateTo(string tag)
    {
        var item = Nav.MenuItems.OfType<NavigationViewItem>()
            .Concat(Nav.FooterMenuItems.OfType<NavigationViewItem>())
            .FirstOrDefault(i => (string?)i.Tag == tag);
        if (item is null) return;
        if (!ReferenceEquals(Nav.SelectedItem, item)) Nav.SelectedItem = item;
        ShowPage(tag);
    }

    private void Nav_OnSelectionChanged(object? sender, NavigationViewSelectionChangedEventArgs e)
    {
        if (e.SelectedItem is NavigationViewItem { Tag: string tag }) ShowPage(tag);
    }

    private void ShowPage(string tag)
    {
        if (!_pages.TryGetValue(tag, out var page))
        {
            page = tag switch
            {
                "connection" => new ConnectionPage(),
                "control" => new ControlPage(),
                "settings" => new SettingsPage(),
                "log" => new LogPage(),
                _ => new ConnectionPage(),
            };
            _pages[tag] = page;
            L10n.Apply(page);
        }
        PageHost.Content = page;
    }

    private void ApplyBackdrop()
    {
        if (ActualTransparencyLevel == WindowTransparencyLevel.Mica)
        {
            Background = Brushes.Transparent;
        }
        else if (this.TryFindResource("SolidBackgroundFillColorBaseBrush", ActualThemeVariant, out var brush) && brush is IBrush b)
        {
            Background = b;
        }
    }
}
