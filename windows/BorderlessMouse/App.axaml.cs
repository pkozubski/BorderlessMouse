using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BorderlessMouse.ViewModels;
using BorderlessMouse.Views;

namespace BorderlessMouse;

public partial class App : Application
{
    private MainViewModel? _viewModel;
    private MainWindow? _mainWindow;
    public bool IsExiting { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _viewModel = new MainViewModel();
            _mainWindow = new MainWindow { DataContext = _viewModel };
            desktop.MainWindow = _mainWindow;
            desktop.ShutdownRequested += (_, _) => _viewModel.Shutdown();
            var background = desktop.Args?.Contains(Models.Autostart.BackgroundArgument) == true
                             && _viewModel.StartMinimized;
            if (!background) _mainWindow.Show();
            _viewModel.AttachClipboard(_mainWindow.Clipboard);
            _viewModel.Start();
            if (background) _viewModel.LogBackgroundStart();
            HandleScreenshotFlag(desktop.Args);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Tryb diagnostyczny: `--screenshot plik.png` renderuje okno do PNG i kończy działanie (CI / podgląd UI).</summary>
    private void HandleScreenshotFlag(string[]? args)
    {
        if (args is null || _mainWindow is null) return;
        if (!_mainWindow.IsVisible) _mainWindow.Show();
        var idx = Array.IndexOf(args, "--screenshot");
        if (idx < 0 || idx + 1 >= args.Length) return;
        var path = args[idx + 1];
        DispatcherTimer.RunOnce(() =>
        {
            try
            {
                Capture(path);
                // druga klatka: dół strony (dalsze grupy ustawień)
                var scroll = _mainWindow.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
                if (scroll is not null)
                {
                    scroll.Offset = new Vector(0, scroll.Extent.Height);
                    _mainWindow.UpdateLayout();
                    Capture(Path.ChangeExtension(path, null) + "-bottom" + Path.GetExtension(path));
                }
            }
            finally
            {
                ExitApplication();
            }
        }, TimeSpan.FromSeconds(2));
    }

    private void Capture(string path)
    {
        if (_mainWindow is null) return;
        var size = new PixelSize((int)_mainWindow.Bounds.Width * 2, (int)_mainWindow.Bounds.Height * 2);
        using var bitmap = new RenderTargetBitmap(size, new Vector(192, 192));
        bitmap.Render(_mainWindow);
        bitmap.Save(path);
    }

    public void ShowMainWindow()
    {
        if (_mainWindow is null) return;
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    public void ExitApplication()
    {
        IsExiting = true;
        _viewModel?.Shutdown();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private void TrayIcon_OnClicked(object? sender, EventArgs e) => ShowMainWindow();
    private void ShowWindow_OnClick(object? sender, EventArgs e) => ShowMainWindow();
    private void Exit_OnClick(object? sender, EventArgs e) => ExitApplication();
}
