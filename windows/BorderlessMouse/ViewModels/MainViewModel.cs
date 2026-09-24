using System.Collections.ObjectModel;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using System.Diagnostics;
using System.Net;
using BorderlessMouse.Audio;
using BorderlessMouse.Display;
using BorderlessMouse.Input;
using BorderlessMouse.Models;
using BorderlessMouse.Net;
using BorderlessMouse.Protocol;
using BorderlessMouse.Security;
using BorderlessMouse.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NAudio.Wave;
using static BorderlessMouse.Localization.L10n;

namespace BorderlessMouse.ViewModels;

public sealed record MacSideOption(MacSide Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record EmergencyHotkeyOption(EmergencyHotkey Value, ushort VirtualKey, string Label, bool WithModifiers = false)
{
    public override string ToString() => Label;
}

public partial class MainViewModel : ObservableObject
{
    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#34C759"));
    private static readonly IBrush Orange = new SolidColorBrush(Color.Parse("#FF9F0A"));
    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#FF453A"));
    private static readonly IBrush Gray = new SolidColorBrush(Color.Parse("#8E8E93"));

    private readonly Settings _settings;
    private readonly ControlClient _client = new();
    private readonly DiscoveryClient _discovery = new();
    private readonly AudioReceiver _audioRx = new();
    private readonly DispatcherTimer _statsTimer;
    private AudioPlayer? _player;
    private InputCapture? _capture;
    private JitterBufferProvider? _jitter;
    private ClipboardSync? _clipboardSync;
    private readonly Updater _updater = new();
    private ReleaseInfo? _pendingRelease;
    private DispatcherTimer? _updateTimer;
    private CancellationTokenSource? _connectLoopCts;
    private TaskCompletionSource? _disconnectedTcs;
    private bool _desiredConnection;
    private bool _loading = true;
    private bool _audioRequested;
    private bool _warnedAccessibility;

    // ekran wirtualny Maca
    private readonly VideoReceiver _videoRx = new();
    private DisplayViewer? _viewer;
    private StatusFlags _macFlags;
    /// <summary>Wysłano DISPLAY_START; po błędzie zostaje true, żeby nie ponawiać w pętli.</summary>
    private bool _displayRequested;
    private bool _displayRunning;
    /// <summary>Kursor sterowany z Windowsa jest na ekranie wirtualnym Maca.</summary>
    private bool _displayFocus;
    private NativeMethods.RECT _displayMonitor;
    /// <summary>Tryb okien działa (Windows go chce, a Mac obsługuje).</summary>
    private bool _windowModeActive;
    private MacWindowList? _macWindows;
    /// <summary>Tryb okien: aktywne okno Windows to okno Maca (wtedy widać też pasek menu Maca).</summary>
    private bool _macWindowActive;
    // okna Windows na Macu
    private WinViewStreamer? _winStreamer;
    private WinViewTracker? _winTracker;
    /// <summary>Wysłano WINVIEW_START; po błędzie zostaje true, żeby nie ponawiać w pętli.</summary>
    private bool _winViewRequested;
    private bool _winViewRunning;
    private readonly Stopwatch _winViewStatsClock = new();
    private long _winViewStatsBytes;
    private long _winViewStatsFrames;
    private readonly Stopwatch _displayStatsClock = new();
    private long _displayStatsBytes;
    private long _displayStatsFrames;

    public MainViewModel()
    {
        _settings = Settings.Load();

        MacSides = new[]
        {
            new MacSideOption(MacSide.Left, T("Po lewej", "Left")),
            new MacSideOption(MacSide.Right, T("Po prawej", "Right")),
            new MacSideOption(MacSide.Top, T("U góry", "Above")),
            new MacSideOption(MacSide.Bottom, T("Na dole", "Below")),
        };
        EmergencyHotkeys = new[]
        {
            new EmergencyHotkeyOption(EmergencyHotkey.ScrollLock, NativeMethods.VK_SCROLL, "Scroll Lock"),
            new EmergencyHotkeyOption(EmergencyHotkey.Pause, NativeMethods.VK_PAUSE, "Pause / Break"),
            new EmergencyHotkeyOption(EmergencyHotkey.F12, NativeMethods.VK_F12, "F12"),
            new EmergencyHotkeyOption(EmergencyHotkey.CtrlAltShiftB, NativeMethods.VK_B, "Ctrl + Alt + Shift + B", WithModifiers: true),
        };

        _hostAddress = _settings.HostAddress;
        _controlPortText = _settings.ControlPort.ToString();
        _audioPortText = _settings.AudioPort.ToString();
        _deviceName = _settings.DeviceName;
        _autoConnect = _settings.AutoConnect;
        _inputSharingEnabled = _settings.InputSharingEnabled;
        _selectedMacSide = MacSides.First(o => o.Value == _settings.MacSide);
        _selectedEmergencyHotkey = EmergencyHotkeys.FirstOrDefault(o => o.Value == _settings.EmergencyHotkey)
                                   ?? EmergencyHotkeys[0];
        _hideCursorWhileRemote = _settings.HideCursorWhileRemote;
        _remoteMouseSpeed = _settings.RemoteMouseSpeed;
        _audioEnabled = _settings.AudioEnabled;
        _jitterBufferMs = _settings.JitterBufferMs;
        _exclusiveMode = _settings.ExclusiveMode;
        _clipboardSyncEnabled = _settings.ClipboardSyncEnabled;
        _displayEnabled = _settings.DisplayEnabled;
        _showMacDisplayWhileRemote = _settings.ShowMacDisplayWhileRemote;
        _displayWindowMode = _settings.DisplayWindowMode;
        _winViewEnabled = _settings.WinViewEnabled;
        _autoCheckUpdates = _settings.AutoCheckUpdates;
        _startMinimized = _settings.StartMinimized;
        _hasCompletedOnboarding = _settings.HasCompletedOnboarding;
        _hasPairingKey = PairingKeyStore.Load() is not null;
        _pairingStatusText = _hasPairingKey
            ? T("Kod zapisany bezpiecznie dla tego konta Windows.", "The code is protected for this Windows account.")
            : T("Wpisz kod wyświetlany w aplikacji na Macu.", "Enter the code shown in the Mac app.");
        // stan autostartu bierzemy z systemu – rejestr jest źródłem prawdy
        _launchAtLogin = Autostart.IsEnabled;
        _autostartStatus = Autostart.StatusDescription;
        Autostart.RefreshIfNeeded();

        RefreshAudioDevices();
        _selectedAudioDevice = AudioDevices.FirstOrDefault(d => d.Id == _settings.AudioDeviceId) ?? AudioDevices[0];

        _client.Connected += name => Post(() => OnConnected(name));
        _client.Disconnected += reason => Post(() => OnDisconnected(reason));
        _client.MessageReceived += (type, payload) => Post(() => OnMessage(type, payload));
        _client.RttMeasured += rtt => Post(() => RttMs = rtt);
        _discovery.PeerFound += peer => Post(() => OnPeerFound(peer));
        _videoRx.FrameReceived += frame => _viewer?.Enqueue(frame);
        _videoRx.Closed += reason => Post(() => OnDisplayFailed(reason ?? T("Strumień ekranu zakończony.", "The display stream ended.")));

        RecoverWinViewMonitor();
        _statsTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => UpdateStats());
        _loading = false;
        UpdateStatus();
    }

    // ------------------------------------------------------------------
    // Właściwości bindowane – ustawienia
    // ------------------------------------------------------------------

    public IReadOnlyList<MacSideOption> MacSides { get; }
    public IReadOnlyList<EmergencyHotkeyOption> EmergencyHotkeys { get; }
    public ObservableCollection<AudioDeviceInfo> AudioDevices { get; } = new();
    public ObservableCollection<DiscoveredPeer> DiscoveredPeers { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    [ObservableProperty] private string _hostAddress;
    [ObservableProperty] private string _controlPortText;
    [ObservableProperty] private string _audioPortText;
    [ObservableProperty] private string _deviceName;
    [ObservableProperty] private bool _autoConnect;
    [ObservableProperty] private bool _inputSharingEnabled;
    [ObservableProperty] private MacSideOption _selectedMacSide;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(EmergencyHotkeyDescription))] private EmergencyHotkeyOption _selectedEmergencyHotkey;
    [ObservableProperty] private bool _hideCursorWhileRemote;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RemoteMouseSpeedLabel))] private double _remoteMouseSpeed;
    [ObservableProperty] private bool _audioEnabled;
    [ObservableProperty] private AudioDeviceInfo _selectedAudioDevice;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(JitterBufferLabel))] private double _jitterBufferMs;
    [ObservableProperty] private bool _exclusiveMode;
    [ObservableProperty] private bool _clipboardSyncEnabled;
    [ObservableProperty] private bool _displayEnabled;
    [ObservableProperty] private bool _showMacDisplayWhileRemote;
    [ObservableProperty] private bool _displayWindowMode;
    [ObservableProperty] private bool _winViewEnabled;
    [ObservableProperty] private bool _winViewDriverMissing;
    [ObservableProperty] private bool _isInstallingDriver;
    [ObservableProperty] private bool _autoCheckUpdates;
    [ObservableProperty] private bool _launchAtLogin;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _hasCompletedOnboarding;
    [ObservableProperty] private string _pairingCode = "";
    [ObservableProperty] private bool _hasPairingKey;
    [ObservableProperty] private bool _pairingCodeInvalid;
    [ObservableProperty] private string _pairingStatusText = "";
    [ObservableProperty] private string _autostartStatus = "";
    [ObservableProperty] private DiscoveredPeer? _selectedPeer;

    // ------------------------------------------------------------------
    // Stan
    // ------------------------------------------------------------------

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ConnectButtonText))] private bool _isConnected;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ConnectButtonText))] private bool _isConnecting;
    [ObservableProperty] private string _statusText = T("Rozłączono", "Disconnected");
    [ObservableProperty] private IBrush _statusBrush = Gray;
    [ObservableProperty] private string _connectionInfo = T("Nie połączono.", "Not connected.");
    [ObservableProperty] private string _macStatusText = "";
    [ObservableProperty] private string _peerName = "";
    [ObservableProperty] private double _rttMs;
    [ObservableProperty] private bool _cursorOnMac;
    [ObservableProperty] private string _cursorStatusText = T("Na tym komputerze", "On this PC");
    [ObservableProperty] private IBrush _cursorStatusBrush = Gray;
    [ObservableProperty] private bool _audioActive;
    [ObservableProperty] private string _audioStatusText = T("Nieaktywne", "Inactive");
    [ObservableProperty] private IBrush _audioStatusBrush = Gray;
    [ObservableProperty] private float _audioLevel;
    [ObservableProperty] private string _audioStatsText = "";
    [ObservableProperty] private bool _hasDiscoveredPeers;
    [ObservableProperty] private string _clipboardStatusText = T("Brak synchronizacji w tej sesji", "No synchronization in this session");
    [ObservableProperty] private bool _macAccessibilityMissing;
    [ObservableProperty] private string _displayStatusText = T("Nieaktywny – uruchamia się po połączeniu z Makiem.", "Inactive — starts after connecting to the Mac.");
    [ObservableProperty] private IBrush _displayStatusBrush = Gray;
    [ObservableProperty] private string _displayStatsText = "";
    [ObservableProperty] private string _winViewStatusText = T("Nieaktywne – uruchamia się po połączeniu z Makiem.", "Inactive — starts after connecting to the Mac.");
    [ObservableProperty] private IBrush _winViewStatusBrush = Gray;
    [ObservableProperty] private string _winViewStatsText = "";

    // aktualizacje
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private string _updateMessage = "";
    [ObservableProperty] private string _updateStatusText = T("Kliknij „Sprawdź teraz”, aby sprawdzić nowe wydania na GitHubie.", "Select Check now to look for a new release.");
    [ObservableProperty] private bool _isCheckingUpdates;
    [ObservableProperty] private bool _isInstallingUpdate;
    [ObservableProperty] private double _updateProgress;

    public string VersionLabel => T($"Wersja {Updater.CurrentVersion}", $"Version {Updater.CurrentVersion}");

    public string ConnectButtonText => IsConnected || IsConnecting ? T("Rozłącz", "Disconnect") : T("Połącz", "Connect");
    public string RemoteMouseSpeedLabel => T($"{RemoteMouseSpeed:0.00}× – Raw Input nie ma akceleracji Windows, więc dostrój tempo kursora na Macu.", $"{RemoteMouseSpeed:0.00}× — adjust the Mac pointer speed because Raw Input has no Windows acceleration.");
    public string JitterBufferLabel => T($"{(int)JitterBufferMs} ms – mniej = niższe opóźnienie, więcej = odporność na zakłócenia.", $"{(int)JitterBufferMs} ms — lower for latency, higher for resilience.");
    public string EmergencyHotkeyDescription => T(
        $"{SelectedEmergencyHotkey.Label} zawsze przełącza sterowanie ręcznie w obie strony.",
        $"{SelectedEmergencyHotkey.Label} always switches control manually in either direction.");

    private static void Post(Action action) => Dispatcher.UIThread.Post(action);

    // ------------------------------------------------------------------
    // Cykl życia
    // ------------------------------------------------------------------

    /// <summary>
    /// Tryb offline (zrzuty ekranu / podgląd UI): bez wykrywania, łączenia,
    /// audio i sprawdzania aktualizacji; interfejs wypełniony przykładowymi danymi.
    /// </summary>
    public void StartOffline()
    {
        _loading = true;
        AutoConnect = false;
        _loading = false;
        Log(T("Tryb podglądu – sieć wyłączona", "Preview mode — network disabled"));
        IsConnected = true;
        HasPairingKey = true;
        PairingStatusText = T("Kod jest chroniony dla tego konta Windows.", "The code is protected for this Windows account.");
        HasCompletedOnboarding = true;
        PeerName = "MacBook Air";
        DiscoveredPeers.Add(new DiscoveredPeer("MacBook Air", "192.168.1.42", 47800));
        HasDiscoveredPeers = true;
        HostAddress = "192.168.1.42";
        MacStatusText = T("Mac: uprawnienie Dostępność OK · przechwytuje dźwięk", "Mac: Accessibility ready · audio capture active");
        CursorOnMac = true;
        AudioActive = true;
        AudioStatusText = T("48000 Hz · stereo · Słuchawki (USB) · WASAPI shared · 15 ms", "48000 Hz · stereo · Headphones (USB) · WASAPI shared · 15 ms");
        AudioStatusBrush = Green;
        AudioLevel = 0.42f;
        AudioStatsText = T("bufor 21 ms · pakiety 18432 · utracone 0 · odrzucone 0 · niedopełnienia 0 · przepełnienia 0", "buffer 21 ms · packets 18432 · lost 0 · rejected 0 · underruns 0 · overruns 0");
        ClipboardStatusText = T("Odebrano 128 zn. z Maca · 21:40:12", "Received 128 characters from Mac · 21:40:12");
        DisplayStatusText = T("Działa · 2560×1440 · kursor na ekranie Maca", "Running · 2560×1440 · pointer on the Mac display");
        DisplayStatusBrush = Green;
        DisplayStatsText = T("60 kl./s · 6,4 Mb/s · dekodowanie GPU", "60 fps · 6.4 Mb/s · GPU decoding");
        UpdateStatusText = T($"Masz najnowszą wersję · sprawdzono {DateTime.Now:HH:mm}", $"Up to date · checked {DateTime.Now:HH:mm}");
        ConnectionInfo = T("Połączono z MacBook Air · 192.168.1.42:47800 · ping 0,4 ms", "Connected to MacBook Air · 192.168.1.42:47800 · ping 0.4 ms");
        StatusText = T("Sterujesz Makiem", "Controlling Mac");
        StatusBrush = Green;
        CursorStatusText = T("Na Macu · wysłane ruchy: 1 284", "On Mac · pointer events: 1,284");
        CursorStatusBrush = Green;
        Log(T("Połączono z MacBook Air (192.168.1.42)", "Connected to MacBook Air (192.168.1.42)"));
        Log(T("Hooki aktywne, ruch myszy z Raw Input (kursor zostaje przy krawędzi)", "Hooks active with Raw Input; the pointer remains at the edge"));
        Log(T("Audio gra: 48000 Hz · stereo · WASAPI shared · 15 ms", "Audio playing: 48000 Hz · stereo · WASAPI shared · 15 ms"));
    }

    public void Start()
    {
        Log(T("Start aplikacji", "Application started"));
        try
        {
            _discovery.Start();
        }
        catch (Exception ex)
        {
            Log(T("Nie udało się uruchomić wykrywania: ", "Discovery could not start: ") + ex.Message);
        }
        _statsTimer.Start();
        if (AutoConnect && HasPairingKey && !string.IsNullOrWhiteSpace(HostAddress))
        {
            BeginConnect();
        }
        ScheduleUpdateChecks();
    }

    // ------------------------------------------------------------------
    // Aktualizacje (GitHub Releases)
    // ------------------------------------------------------------------

    private void ScheduleUpdateChecks()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            Post(() => { if (AutoCheckUpdates) _ = CheckUpdatesAsync(silent: true); });
        });
        // Wersja testowa sprawdza często i instaluje sama – nie trzeba pobierać każdego buildu.
        var interval = Updater.IsDevChannel ? TimeSpan.FromMinutes(2) : TimeSpan.FromHours(6);
        _updateTimer = new DispatcherTimer(interval, DispatcherPriority.Background,
            (_, _) => { if (AutoCheckUpdates) _ = CheckUpdatesAsync(silent: true); });
        _updateTimer.Start();
    }

    [RelayCommand]
    private Task CheckUpdates() => CheckUpdatesAsync(silent: false);

    private async Task CheckUpdatesAsync(bool silent)
    {
        if (IsCheckingUpdates || IsInstallingUpdate) return;
        IsCheckingUpdates = true;
        UpdateStatusText = T("Sprawdzanie…", "Checking…");
        try
        {
            var release = await _updater.CheckAsync(CancellationToken.None);
            if (release is null)
            {
                UpdateStatusText = T("Brak wydań na GitHubie.", "No releases found.");
            }
            else if (Updater.IsNewer(release.Version, Updater.CurrentVersion))
            {
                _pendingRelease = release;
                UpdateAvailable = true;
                UpdateMessage = T($"Wersja {release.Version} jest gotowa do pobrania (masz {Updater.CurrentVersion}).", $"Version {release.Version} is ready to download (current {Updater.CurrentVersion}).");
                UpdateStatusText = T($"Dostępna wersja {release.Version} · {release.PageUrl}", $"Version {release.Version} available · {release.PageUrl}");
                Log(T($"Dostępna aktualizacja {release.Version}", $"Update {release.Version} is available"));
                // Kanał testowy: od razu, ale nie gdy kursor jest na Macu (restart zabrałby sterowanie).
                if (Updater.IsDevChannel && !CursorOnMac) _ = InstallUpdate();
            }
            else
            {
                UpdateStatusText = T($"Masz najnowszą wersję · sprawdzono {DateTime.Now:HH:mm}", $"Up to date · checked {DateTime.Now:HH:mm}");
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText = silent ? UpdateStatusText : T("Nie udało się sprawdzić: ", "Check failed: ") + ex.Message;
            if (!silent) Log("Aktualizacje: " + ex.Message);
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    [RelayCommand]
    private async Task InstallUpdate()
    {
        if (_pendingRelease is null || IsInstallingUpdate) return;
        IsInstallingUpdate = true;
        UpdateProgress = 0;
        UpdateStatusText = T("Pobieranie aktualizacji…", "Downloading update…");
        try
        {
            var progress = new Progress<double>(p => UpdateProgress = p);
            await _updater.DownloadAndInstallAsync(_pendingRelease, progress, CancellationToken.None);
            UpdateStatusText = T("Instalowanie – aplikacja uruchomi się ponownie.", "Installing — the app will restart.");
            Log(T("Aktualizacja pobrana, restart…", "Update downloaded; restarting…"));
            await Task.Delay(500);
            (Avalonia.Application.Current as App)?.ExitApplication();
        }
        catch (Exception ex)
        {
            UpdateStatusText = T("Błąd aktualizacji: ", "Update failed: ") + ex.Message;
            Log(UpdateStatusText);
            IsInstallingUpdate = false;
        }
    }

    partial void OnAutoCheckUpdatesChanged(bool value)
    {
        _settings.AutoCheckUpdates = value;
        SaveSettings();
    }

    partial void OnLaunchAtLoginChanged(bool value)
    {
        if (_loading) return;
        if (!OperatingSystem.IsWindows())
        {
            AutostartStatus = T("Dostępne tylko na Windows.", "Available only on Windows.");
            return;
        }
        try
        {
            Log(Autostart.SetEnabled(value));
        }
        catch (Exception ex)
        {
            Log("Autostart: " + ex.Message);
        }
        AutostartStatus = Autostart.StatusDescription;
    }

    partial void OnStartMinimizedChanged(bool value)
    {
        _settings.StartMinimized = value;
        SaveSettings();
    }

    /// <summary>Wpis do dziennika, gdy aplikacja wystartowała do zasobnika.</summary>
    public void LogBackgroundStart() => Log(T("Start w tle (autostart) – okno ukryte, ikona w zasobniku", "Started in the background; the app is available in the tray"));

    /// <summary>Podpina schowek okna głównego (dostępny dopiero po utworzeniu okna).</summary>
    public void AttachClipboard(IClipboard? clipboard)
    {
        if (clipboard is null || _clipboardSync is not null) return;
        _clipboardSync = new ClipboardSync(clipboard) { Enabled = ClipboardSyncEnabled };
        _clipboardSync.LocalChanged += content =>
        {
            if (!IsConnected || !ClipboardSyncEnabled) return;
            _client.SendClipboard(content);
            ClipboardStatusText = T($"Wysłano {content.Summary} do Maca · {DateTime.Now:HH:mm:ss}", $"Sent {content.Summary} to Mac · {DateTime.Now:HH:mm:ss}");
        };
        _clipboardSync.Error += message => ClipboardStatusText = message;
        _clipboardSync.Start();
    }

    public void Shutdown()
    {
        _clipboardSync?.Stop();
        _desiredConnection = false;
        _connectLoopCts?.Cancel();
        _statsTimer.Stop();
        _updateTimer?.Stop();
        StopAudio(notifyMac: true);
        StopDisplay(notifyMac: true);
        StopWinView(notifyMac: true);
        _winStreamer?.Dispose();
        _viewer?.Dispose();
        _viewer = null;
        _capture?.Dispose();
        _capture = null;
        _client.Disconnect(null);
        _discovery.Stop();
        SaveSettings();
    }

    // ------------------------------------------------------------------
    // Połączenie
    // ------------------------------------------------------------------

    [RelayCommand]
    private void ToggleConnect()
    {
        if (IsConnected || IsConnecting)
        {
            _desiredConnection = false;
            _connectLoopCts?.Cancel();
            _client.Disconnect(null);
            IsConnecting = false;
            UpdateStatus();
        }
        else
        {
            BeginConnect();
        }
    }

    private void BeginConnect()
    {
        if (!HasPairingKey)
        {
            PairingCodeInvalid = true;
            PairingStatusText = T("Najpierw wpisz kod parowania z Maca.", "Enter the pairing code from your Mac first.");
            Log(PairingStatusText);
            return;
        }
        if (string.IsNullOrWhiteSpace(HostAddress))
        {
            Log(T("Podaj adres IP Maca lub wybierz go z listy.", "Enter the Mac IP address or select a discovered Mac."));
            return;
        }
        _desiredConnection = true;
        _connectLoopCts?.Cancel();
        var cts = new CancellationTokenSource();
        _connectLoopCts = cts;
        _ = ConnectLoopAsync(cts.Token);
    }

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _desiredConnection)
        {
            var host = HostAddress.Trim();
            var port = _settings.ControlPort;
            IsConnecting = true;
            UpdateStatus();
            _disconnectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                Log(T($"Łączenie z {host}:{port}…", $"Connecting to {host}:{port}…"));
                var pairingKey = PairingKeyStore.Load();
                if (pairingKey is null)
                {
                    Post(() =>
                    {
                        HasPairingKey = false;
                        PairingCodeInvalid = true;
                        PairingStatusText = T("Nie można odczytać kodu parowania. Wpisz go ponownie.", "The pairing code could not be read. Enter it again.");
                    });
                    break;
                }
                await _client.ConnectAsync(host, port, DeviceName, pairingKey, ct);
                await _disconnectedTcs.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log(T("Nie udało się połączyć: ", "Connection failed: ") + ex.Message);
            }
            IsConnecting = false;
            UpdateStatus();
            if (!_desiredConnection || !AutoConnect) break;
            try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { break; }
        }
        IsConnecting = false;
        UpdateStatus();
    }

    private void OnConnected(string macName)
    {
        IsConnected = true;
        IsConnecting = false;
        PeerName = string.IsNullOrWhiteSpace(macName) ? _client.RemoteAddress : macName;
        PairingCodeInvalid = false;
        PairingStatusText = T($"Bezpiecznie połączono z {PeerName}.", $"Securely connected to {PeerName}.");
        Log(T($"Połączono z {PeerName} ({_client.RemoteAddress})", $"Connected to {PeerName} ({_client.RemoteAddress})"));
        UpdateStatus();
        SetupInputCapture();
        if (AudioEnabled) StartAudio();
    }

    private void OnDisconnected(string? reason)
    {
        var was = IsConnected;
        IsConnected = false;
        CursorOnMac = false;
        _capture?.ReturnToLocal(0.5f, sendRelease: false);
        ApplyHookState(); // bez połączenia hooki są zbędne
        StopAudio(notifyMac: false);
        StopDisplay(notifyMac: false);
        StopWinView(notifyMac: false);
        _macFlags = StatusFlags.None;
        SetWinViewStatus(T("Nieaktywne – uruchamia się po połączeniu z Makiem.", "Inactive — starts after connecting to the Mac."), Gray);
        SetDisplayStatus(T("Nieaktywny – uruchamia się po połączeniu z Makiem.", "Inactive — starts after connecting to the Mac."), Gray);
        MacStatusText = "";
        MacAccessibilityMissing = false;
        var pairingFailure = reason?.Contains("kod parowania", StringComparison.OrdinalIgnoreCase) == true
                             || reason?.Contains("pairing", StringComparison.OrdinalIgnoreCase) == true;
        if (pairingFailure)
        {
            // Błędny sekret nie jest zwykłą awarią sieci. Nie zapętlamy prób,
            // żeby nie generować ruchu podobnego do ataku brute-force.
            _desiredConnection = false;
            _connectLoopCts?.Cancel();
            PairingCodeInvalid = true;
            PairingStatusText = reason ?? T("Mac odrzucił kod parowania.", "The Mac rejected the pairing code.");
        }
        if (was || reason is not null) Log(reason is null ? T("Rozłączono", "Disconnected") : T("Rozłączono: ", "Disconnected: ") + reason);
        UpdateStatus();
        _disconnectedTcs?.TrySetResult();
    }

    private void OnMessage(MessageType type, byte[] payload)
    {
        switch (type)
        {
            case MessageType.Leave:
                if (Frame.ParseLeave(payload) is { } leave)
                {
                    var fromDisplay = Frame.LeaveFromVirtualDisplay(payload) && _displayRunning;
                    _capture?.ReturnToLocal(leave.ratio, sendRelease: true, fromDisplay ? _displayMonitor : null);
                    _displayFocus = false;
                    UpdateViewerVisibility();
                }
                break;
            case MessageType.DisplayReady:
                if (Frame.ParseDisplayReady(payload) is { } ready) OnDisplayReady(ready);
                break;
            case MessageType.DisplayWindows:
                if (_displayRunning && _windowModeActive && Frame.ParseDisplayWindows(payload) is { } windows) ApplyMacWindows(windows);
                break;
            case MessageType.CursorShape:
                if (_displayRunning && _windowModeActive && payload.Length >= 1) _viewer?.SetCursorShape(payload[0]);
                break;
            case MessageType.WindowMenu:
                if (_displayRunning && _windowModeActive && Frame.ParseWindowMenu(payload) is { } menu) _viewer?.SetMenu(menu.pid, menu.items);
                break;
            case MessageType.WindowIcon:
                if (_displayRunning && Frame.ParseWindowIcon(payload) is { } icon) _viewer?.SetIcon(icon.pid, icon.png);
                break;
            case MessageType.WindowHandoff:
                if (_displayRunning && _windowModeActive && Frame.ParseWindowHandoff(payload) is { } handoff)
                    _capture?.HandoffToWindow(ToScreen(handoff.x, handoff.y));
                break;
            case MessageType.DisplayFocus:
                if (Frame.ParseDisplayFocus(payload) is { } focus)
                {
                    _displayFocus = focus;
                    UpdateViewerVisibility();
                }
                break;
            case MessageType.AudioFormat:
                if (Frame.ParseAudioFormat(payload) is { } fmt) OnAudioFormat(fmt);
                break;
            case MessageType.WinViewReady:
                if (Frame.ParseWinViewReady(payload) is { } winReady) _ = OnWinViewReadyAsync(winReady);
                break;
            case MessageType.WinViewKeyframe:
                _winStreamer?.RequestKeyframe();
                _winTracker?.Resend();
                break;
            case MessageType.WinViewPointerEnter:
                if (_winViewRunning && _winTracker is { } tracker && Frame.ParseWinViewPointerEnter(payload) is { } enter)
                {
                    var m = tracker.Monitor;
                    _capture?.EnterWinView(new NativeMethods.POINT { X = m.Left + enter.x, Y = m.Top + enter.y });
                }
                break;
            case MessageType.Clipboard:
                if (ClipboardSyncEnabled && _clipboardSync is not null && Frame.ParseClipboard(payload) is { } content)
                {
                    _ = ApplyClipboardAsync(content);
                }
                break;
            case MessageType.Status:
                if (payload.Length >= 1)
                {
                    var flags = (StatusFlags)payload[0];
                    var displayFlagsChanged = (flags & DisplayFlagsMask) != (_macFlags & DisplayFlagsMask);
                    var winViewChanged = (flags & StatusFlags.WinViewSupported) != (_macFlags & StatusFlags.WinViewSupported);
                    _macFlags = flags;
                    if (displayFlagsChanged) EvaluateDisplay();
                    if (winViewChanged) EvaluateWinView();
                    var ax = flags.HasFlag(StatusFlags.AccessibilityGranted);
                    MacAccessibilityMissing = !ax;
                    MacStatusText = ax
                        ? T("Mac: uprawnienie Dostępność OK", "Mac: Accessibility ready")
                          + (flags.HasFlag(StatusFlags.CursorOnMac) ? T(" · Mac potwierdza sterowanie", " · control active") : "")
                          + (flags.HasFlag(StatusFlags.AudioCapturing) ? T(" · przechwytuje dźwięk", " · audio capture active") : "")
                        : T("Mac: BRAK uprawnienia Dostępność – nadaj je w Ustawieniach systemowych Maca (Prywatność i ochrona → Dostępność).", "Mac: Accessibility permission is missing. Enable it in System Settings → Privacy & Security → Accessibility.");
                    if (!ax && !_warnedAccessibility)
                    {
                        _warnedAccessibility = true;
                        Log(T("Mac zgłasza brak uprawnienia Dostępność – sterowanie nie zadziała, dopóki go nie nadasz.", "The Mac reports missing Accessibility permission; control is unavailable until it is granted."));
                    }
                }
                break;
        }
    }

    private void OnPeerFound(DiscoveredPeer peer)
    {
        var existing = DiscoveredPeers.FirstOrDefault(p => p.Address == peer.Address);
        if (existing is null)
        {
            DiscoveredPeers.Add(peer);
            Log(T($"Znaleziono Maca: {peer.Name} ({peer.Address})", $"Found Mac: {peer.Name} ({peer.Address})"));
            if (string.IsNullOrWhiteSpace(HostAddress))
            {
                HostAddress = peer.Address;
                if (AutoConnect && HasPairingKey && !_desiredConnection) BeginConnect();
            }
        }
        else if (existing.Name != peer.Name || existing.Port != peer.Port)
        {
            var idx = DiscoveredPeers.IndexOf(existing);
            DiscoveredPeers[idx] = peer;
        }
        HasDiscoveredPeers = DiscoveredPeers.Count > 0;
    }

    partial void OnSelectedPeerChanged(DiscoveredPeer? value)
    {
        if (value is null) return;
        HostAddress = value.Address;
        if (value.Port != _settings.ControlPort) ControlPortText = value.Port.ToString();
        if (HasPairingKey && !IsConnected && !IsConnecting) BeginConnect();
    }

    private void UpdateStatus()
    {
        if (IsConnected)
        {
            StatusText = CursorOnMac ? T("Sterujesz Makiem", "Controlling Mac") : T($"Połączono z {PeerName}", $"Connected to {PeerName}");
            StatusBrush = Green;
            var address = string.IsNullOrWhiteSpace(_client.RemoteAddress) ? HostAddress : _client.RemoteAddress;
            ConnectionInfo = T($"Połączono z {PeerName}", $"Connected to {PeerName}") + $" · {address}:{_settings.ControlPort}" + (RttMs > 0 ? $" · ping {RttMs:0.0} ms" : "");
        }
        else if (IsConnecting)
        {
            StatusText = T("Łączenie…", "Connecting…");
            StatusBrush = Orange;
            ConnectionInfo = T($"Próba połączenia z {HostAddress}:{_settings.ControlPort}…", $"Connecting to {HostAddress}:{_settings.ControlPort}…");
        }
        else
        {
            StatusText = T("Rozłączono", "Disconnected");
            StatusBrush = Gray;
            ConnectionInfo = T("Nie połączono.", "Not connected.");
        }
        CursorStatusText = CursorOnMac
            ? T($"Na Macu · wysłane ruchy: {_capture?.RemoteMovesSent ?? 0}", $"On Mac · pointer events: {_capture?.RemoteMovesSent ?? 0}")
            : T("Na tym komputerze", "On this PC");
        CursorStatusBrush = CursorOnMac ? Green : Gray;
    }

    // ------------------------------------------------------------------
    // Klawiatura i mysz
    // ------------------------------------------------------------------

    private void SetupInputCapture()
    {
        if (!OperatingSystem.IsWindows())
        {
            Log(T("Przechwytywanie klawiatury/myszy działa tylko na Windows.", "Keyboard and pointer capture is available only on Windows."));
            return;
        }
        if (_capture is null)
        {
            _capture = new InputCapture(_client);
            _capture.Log += Log;
            _capture.RemoteChanged += remote =>
            {
                CursorOnMac = remote;
                if (!remote) _displayFocus = false; // Mac potwierdzi stan przy następnym wejściu
                // Okno Maca przeciągnięte za pasek do krawędzi po stronie Maca: wraca na Maca
                // (tam, gdzie pojawia się kursor), a przeciąganie na Windowsie się kończy.
                if (remote && _viewer?.MovingWindowId is > 0 and var movingId && _client.IsConnected)
                {
                    _client.Send(Frame.WindowReturn(movingId, _capture!.LastEnterRatio));
                    _viewer.CancelMove();
                }
                UpdateStatus();
                UpdateViewerVisibility();
            };
            _capture.MacWindowAt = point =>
            {
                var proxies = _viewer?.Proxies;
                if (proxies is null || proxies.Count == 0) return null;
                var root = DisplayNative.GetAncestor(DisplayNative.WindowFromPoint(point), DisplayNative.GA_ROOT);
                if (!proxies.TryGetValue(root, out var proxy)) return null;
                var c = proxy.Client;
                if (point.X < c.Left || point.X >= c.Right || point.Y < c.Top || point.Y >= c.Bottom) return null;
                var (x, y) = MapToMac(proxy, point);
                return (proxy.Id, x, y);
            };
            _capture.MapToMacWindow = (id, point) =>
            {
                foreach (var proxy in _viewer?.Proxies.Values ?? Enumerable.Empty<DisplayViewer.ProxyInfo>())
                    if (proxy.Id == id) return MapToMac(proxy, point);
                return null;
            };
            _capture.DraggingMacWindow = () => _viewer?.MovingWindowId is > 0;
            _capture.MacWindowIsForeground = () =>
                _viewer?.Proxies.TryGetValue(NativeMethods.GetForegroundWindow(), out var proxy) == true && !proxy.Popup;
        }
        _capture.Enabled = InputSharingEnabled;
        _capture.Side = SelectedMacSide.Value;
        _capture.EmergencyVirtualKey = SelectedEmergencyHotkey.VirtualKey;
        _capture.EmergencyRequiresModifiers = SelectedEmergencyHotkey.WithModifiers;
        _capture.HideCursorWhileRemote = HideCursorWhileRemote;
        _capture.RemoteMouseSpeed = RemoteMouseSpeed;
        ApplyHookState();
    }

    private void ApplyHookState()
    {
        if (_capture is null || !OperatingSystem.IsWindows()) return;
        try
        {
            if (InputSharingEnabled && IsConnected) _capture.InstallHooks();
            else _capture.UninstallHooks();
        }
        catch (Exception ex)
        {
            Log(T("Błąd przechwytywania wejścia: ", "Input capture error: ") + ex.Message);
        }
    }

    // ------------------------------------------------------------------
    // Audio
    // ------------------------------------------------------------------

    [RelayCommand]
    private void RefreshAudioDevices()
    {
        var current = SelectedAudioDevice?.Id;
        AudioDevices.Clear();
        var devices = OperatingSystem.IsWindows()
            ? AudioPlayer.EnumerateOutputDevices()
            : new[] { new AudioDeviceInfo(null, T("Domyślne urządzenie systemowe", "Default system device")) };
        foreach (var d in devices) AudioDevices.Add(d);
        if (!_loading)
        {
            SelectedAudioDevice = AudioDevices.FirstOrDefault(d => d.Id == current) ?? AudioDevices[0];
        }
    }

    private void StartAudio()
    {
        if (!IsConnected) return;
        if (!OperatingSystem.IsWindows())
        {
            AudioStatusText = T("Odtwarzanie WASAPI dostępne tylko na Windows.", "WASAPI playback is available only on Windows.");
            AudioStatusBrush = Orange;
            return;
        }
        try
        {
            var audioKey = _client.AudioKey;
            var sessionId = _client.AudioSessionId;
            if (audioKey is null || sessionId is null)
                throw new InvalidOperationException(T("Brak bezpiecznej sesji audio.", "No secure audio session is available."));
            var port = _audioRx.Start(_settings.AudioPort, _client.RemoteAddress, audioKey, sessionId.Value);
            _audioRequested = true;
            _client.SendAudioStart((ushort)port);
            AudioStatusText = T($"Poproszono Maca o strumień na port UDP {port}… (przy pierwszym użyciu Mac pokaże okno zgody na nagrywanie dźwięku)", $"Waiting for an encrypted stream on UDP {port}… The Mac may request audio permission.");
            AudioStatusBrush = Orange;
            Log(T($"Audio: nasłuch UDP na porcie {port}, wysłano AUDIO_START", $"Audio: listening on UDP {port}; AUDIO_START sent"));
        }
        catch (Exception ex)
        {
            AudioStatusText = T("Nie można otworzyć portu UDP: ", "Cannot open UDP port: ") + ex.Message;
            AudioStatusBrush = Red;
            Log(AudioStatusText);
        }
    }

    private void OnAudioFormat(AudioFormatInfo fmt)
    {
        if (!fmt.IsOk)
        {
            AudioStatusText = T("Mac odmówił: ", "Mac refused: ") + fmt.Message;
            AudioStatusBrush = Red;
            Log(AudioStatusText);
            _audioRx.Stop();
            _audioRequested = false;
            return;
        }
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var format = new WaveFormat(fmt.SampleRate, 16, fmt.Channels);
            _jitter = new JitterBufferProvider(format, (int)JitterBufferMs);
            _audioRx.Provider = _jitter;
            _player ??= new AudioPlayer();
            var desc = _player.Start(SelectedAudioDevice?.Id, _jitter, ExclusiveMode, ExclusiveMode ? 5 : 15);
            AudioActive = true;
            AudioStatusText = $"{fmt.SampleRate} Hz · {(fmt.Channels == 2 ? "stereo" : T(fmt.Channels + " kan.", fmt.Channels + " ch."))} · {desc}";
            AudioStatusBrush = Green;
            Log(T("Audio gra: ", "Audio playing: ") + AudioStatusText);
        }
        catch (Exception ex)
        {
            AudioStatusText = T("Błąd WASAPI: ", "WASAPI error: ") + ex.Message;
            AudioStatusBrush = Red;
            Log(AudioStatusText);
        }
    }

    private void StopAudio(bool notifyMac)
    {
        if (notifyMac && _audioRequested && _client.IsConnected) _client.SendAudioStop();
        _audioRequested = false;
        _player?.Stop();
        _audioRx.Stop();
        _audioRx.Provider = null;
        _jitter = null;
        if (AudioActive) Log(T("Audio zatrzymane", "Audio stopped"));
        AudioActive = false;
        AudioLevel = 0;
        AudioStatusText = T("Nieaktywne", "Inactive");
        AudioStatusBrush = Gray;
        AudioStatsText = "";
    }

    private void RestartPlayer()
    {
        if (!AudioActive || _jitter is null || _player is null || !OperatingSystem.IsWindows()) return;
        try
        {
            _jitter.Clear();
            var desc = _player.Start(SelectedAudioDevice?.Id, _jitter, ExclusiveMode, ExclusiveMode ? 5 : 15);
            AudioStatusText = $"{_jitter.WaveFormat.SampleRate} Hz · {(_jitter.WaveFormat.Channels == 2 ? "stereo" : T(_jitter.WaveFormat.Channels + " kan.", _jitter.WaveFormat.Channels + " ch."))} · {desc}";
            Log(T("Audio: zmieniono urządzenie/tryb – ", "Audio output or mode changed — ") + desc);
        }
        catch (Exception ex)
        {
            AudioStatusText = T("Błąd WASAPI: ", "WASAPI error: ") + ex.Message;
            AudioStatusBrush = Red;
            Log(AudioStatusText);
        }
    }

    private void UpdateStats()
    {
        if (CursorOnMac) UpdateStatus();
        UpdateDisplayStats();
        UpdateWinViewStats();
        if (!AudioActive || _jitter is null)
        {
            AudioLevel = 0;
            return;
        }
        AudioLevel = _audioRx.Level;
        AudioStatsText = T(
            $"bufor {_jitter.BufferedMs} ms · pakiety {_audioRx.PacketsReceived} · utracone {_audioRx.PacketsLost} · odrzucone {_audioRx.PacketsRejected} · niedopełnienia {_jitter.Underruns} · przepełnienia {_jitter.Overruns}",
            $"buffer {_jitter.BufferedMs} ms · packets {_audioRx.PacketsReceived} · lost {_audioRx.PacketsLost} · rejected {_audioRx.PacketsRejected} · underruns {_jitter.Underruns} · overruns {_jitter.Overruns}");
        if (IsConnected) UpdateStatus();
    }

    // ------------------------------------------------------------------
    // Okna Windows na Macu
    // ------------------------------------------------------------------

    private void SetWinViewStatus(string text, IBrush brush)
    {
        WinViewStatusText = text;
        WinViewStatusBrush = brush;
    }

    /// <summary>Uruchamia okna Windows na Macu, gdy obie strony je obsługują i jest sterownik.</summary>
    private void EvaluateWinView()
    {
        if (!IsConnected || !OperatingSystem.IsWindows()) return;
        if (!WinViewEnabled)
        {
            SetWinViewStatus(T("Wyłączone w ustawieniach.", "Turned off in settings."), Gray);
            return;
        }
        if (!_macFlags.HasFlag(StatusFlags.WinViewSupported))
        {
            SetWinViewStatus(T("Mac nie obsługuje okien Windows – zaktualizuj aplikację na Macu.", "The Mac cannot show Windows apps — update the Mac app."), Gray);
            return;
        }
        if (_winViewRequested) return;
        WinViewDriverMissing = !VirtualDisplayDriver.IsInstalled;
        if (WinViewDriverMissing)
        {
            SetWinViewStatus(T("Potrzebny jest sterownik monitora wirtualnego – kliknij „Zainstaluj”.", "The virtual monitor driver is required — select Install."), Orange);
            return;
        }
        _client.Send(Frame.WinViewStart(InputCapture.EntryEdgeFor(SelectedMacSide.Value)));
        _winViewRequested = true;
        SetWinViewStatus(T("Uruchamianie…", "Starting…"), Orange);
    }

    private async Task OnWinViewReadyAsync(WinViewReadyInfo info)
    {
        try
        {
            if (_winViewRunning && !info.IsOk)
            {
                OnWinViewFailed(T("Mac: ", "Mac: ") + info.Message);
                return;
            }
            if (!_winViewRequested || _winViewRunning || !OperatingSystem.IsWindows()) return;
            if (!info.IsOk)
            {
                StopWinViewLocal();
                SetWinViewStatus(T("Mac: ", "Mac: ") + info.Message, Red);
                if (info.Message.Length > 0) Log(T("Okna Windows: ", "Windows apps: ") + info.Message);
                return;
            }
            var monitor = VirtualDisplayDriver.Find() ?? throw new InvalidOperationException(
                T("Nie znaleziono monitora wirtualnego.", "The virtual monitor was not found."));
            // Monitor wirtualny ma rozmiar ekranu Maca w punktach – okna wyglądają na Macu jak u siebie.
            var width = info.ScreenWidth;
            var height = info.ScreenHeight;
            if (!VirtualDisplayDriver.Modes(monitor.DeviceName).Contains((width, height)) && VirtualDisplayDriver.TryAddMode(width, height))
            {
                for (var i = 0; i < 24; i++)
                {
                    await Task.Delay(250);
                    if (VirtualDisplayDriver.Find() is { } reloaded && VirtualDisplayDriver.Modes(reloaded.DeviceName).Contains((width, height)))
                    {
                        monitor = reloaded;
                        break;
                    }
                }
            }
            if (!_winViewRequested) return;
            (width, height) = ClosestMode(VirtualDisplayDriver.Modes(monitor.DeviceName), width, height);
            DisplayNative.ExcludedArea = monitor.Attached ? monitor.Bounds : null;
            var physical = DisplayNative.Monitors();
            if (physical.Count == 0) throw new InvalidOperationException("No monitors");
            var bounds = Union(physical.Select(m => m.Bounds));
            var (x, y) = SelectedMacSide.Value switch
            {
                MacSide.Left => (bounds.Left - width, physical.Where(m => m.Bounds.Left == bounds.Left).Min(m => m.Bounds.Top)),
                MacSide.Right => (bounds.Right, physical.Where(m => m.Bounds.Right == bounds.Right).Min(m => m.Bounds.Top)),
                MacSide.Top => (physical.Where(m => m.Bounds.Top == bounds.Top).Min(m => m.Bounds.Left), bounds.Top - height),
                _ => (physical.Where(m => m.Bounds.Bottom == bounds.Bottom).Min(m => m.Bounds.Left), bounds.Bottom),
            };
            VirtualDisplayDriver.Attach(monitor.DeviceName, width, height, x, y);
            _settings.WinViewAttached = true;
            SaveSettings();
            var attached = VirtualDisplayDriver.Find();
            if (attached is not { Attached: true } live) throw new InvalidOperationException(
                T("Windows nie podłączył monitora wirtualnego.", "Windows did not attach the virtual monitor."));
            DisplayNative.ExcludedArea = live.Bounds;
            var client = _client;
            _winTracker = new WinViewTracker(live.Bounds, frame => { if (client.IsConnected) client.Send(frame); });
            _winTracker.Start();
            _capture?.SetWinView(_winTracker, Union(DisplayNative.Monitors().Select(m => m.Bounds)));
            if (_winStreamer is null)
            {
                _winStreamer = new WinViewStreamer();
                _winStreamer.Failed += message => Post(() => OnWinViewFailed(message));
            }
            _winStreamer.Start(live.DeviceName, IPAddress.Parse(_client.RemoteAddress), info.Port, info.Key, info.Token);
            _winViewRunning = true;
            _winViewStatsClock.Restart();
            _winViewStatsBytes = _winViewStatsFrames = 0;
            SetWinViewStatus(T($"Działa · monitor {live.Bounds.Width}×{live.Bounds.Height} po stronie Maca",
                $"Running · {live.Bounds.Width}×{live.Bounds.Height} monitor on the Mac side"), Green);
            Log(T($"Okna Windows na Macu: monitor wirtualny {live.Bounds.Width}×{live.Bounds.Height} ({live.DeviceName})",
                $"Windows apps on the Mac: {live.Bounds.Width}×{live.Bounds.Height} virtual monitor ({live.DeviceName})"));
        }
        catch (Exception ex)
        {
            if (_client.IsConnected) _client.Send(Frame.WinViewStop());
            StopWinViewLocal();
            SetWinViewStatus(ex.Message, Red);
            Log(T("Okna Windows: ", "Windows apps: ") + ex.Message);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(info.Key);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(info.Token);
        }
    }

    private static (int width, int height) ClosestMode(List<(int Width, int Height)> modes, int width, int height)
    {
        if (modes.Count == 0 || modes.Contains((width, height))) return (width, height);
        var aspect = (double)width / height;
        return modes.OrderBy(m => Math.Abs((double)m.Width / m.Height - aspect) > 0.02)
            .ThenBy(m => Math.Abs(m.Width * m.Height - width * height)).First();
    }

    private static NativeMethods.RECT Union(IEnumerable<NativeMethods.RECT> rects)
    {
        var list = rects.ToList();
        return new NativeMethods.RECT
        {
            Left = list.Min(r => r.Left), Top = list.Min(r => r.Top),
            Right = list.Max(r => r.Right), Bottom = list.Max(r => r.Bottom),
        };
    }

    private void OnWinViewFailed(string message)
    {
        if (!_winViewRunning) return;
        if (_client.IsConnected) _client.Send(Frame.WinViewStop());
        StopWinViewLocal();
        SetWinViewStatus(message, Red);
        Log(message);
    }

    private void StopWinView(bool notifyMac)
    {
        if (notifyMac && _winViewRequested && _client.IsConnected) _client.Send(Frame.WinViewStop());
        _winViewRequested = false;
        StopWinViewLocal();
    }

    /// <summary>Zatrzymuje strumień i odłącza monitor wirtualny (okna z niego wracają na monitory Windows).</summary>
    private void StopWinViewLocal()
    {
        _winViewRunning = false;
        _capture?.SetWinView(null, default);
        _winTracker?.Stop();
        _winTracker = null;
        _winStreamer?.Stop();
        WinViewStatsText = "";
        if (!OperatingSystem.IsWindows()) return;
        DisplayNative.ExcludedArea = null;
        if (!_settings.WinViewAttached) return;
        try
        {
            if (VirtualDisplayDriver.Find() is { Attached: true } monitor) VirtualDisplayDriver.Detach(monitor.DeviceName);
            _settings.WinViewAttached = false;
            SaveSettings();
        }
        catch (Exception ex)
        {
            Log(T("Nie udało się odłączyć monitora wirtualnego: ", "Could not detach the virtual monitor: ") + ex.Message);
        }
    }

    /// <summary>Po awarii aplikacji monitor wirtualny mógł zostać podłączony – okna nie mogą na nim utknąć.</summary>
    private void RecoverWinViewMonitor()
    {
        if (!OperatingSystem.IsWindows() || !_settings.WinViewAttached) return;
        StopWinViewLocal();
        _settings.WinViewAttached = false;
        _settings.Save();
    }

    [RelayCommand]
    private async Task InstallDisplayDriver()
    {
        if (!OperatingSystem.IsWindows() || IsInstallingDriver) return;
        IsInstallingDriver = true;
        SetWinViewStatus(T("Instalowanie sterownika (potwierdź w oknie Windows)…", "Installing the driver (confirm the Windows prompt)…"), Orange);
        try
        {
            await VirtualDisplayDriver.InstallAsync();
            // Nowy monitor Windows od razu podłącza – do czasu sesji ma być odłączony.
            if (VirtualDisplayDriver.Find() is { Attached: true } monitor) VirtualDisplayDriver.Detach(monitor.DeviceName);
            WinViewDriverMissing = false;
            Log(T("Zainstalowano sterownik monitora wirtualnego.", "Installed the virtual monitor driver."));
            SetWinViewStatus(T("Sterownik zainstalowany.", "Driver installed."), Green);
            EvaluateWinView();
        }
        catch (Exception ex)
        {
            SetWinViewStatus(ex.Message, Red);
            Log(T("Sterownik monitora wirtualnego: ", "Virtual monitor driver: ") + ex.Message);
        }
        finally
        {
            IsInstallingDriver = false;
        }
    }

    // ------------------------------------------------------------------
    // Ekran wirtualny Maca
    // ------------------------------------------------------------------

    private const StatusFlags DisplayFlagsMask = StatusFlags.DisplaySupported | StatusFlags.DisplayEnabled;

    /// <summary>Uruchamia ekran wirtualny, gdy obie strony go obsługują i zezwalają.</summary>
    private void EvaluateDisplay()
    {
        if (!IsConnected) return;
        if (!DisplayEnabled)
        {
            SetDisplayStatus(T("Wyłączony w ustawieniach.", "Turned off in settings."), Gray);
            return;
        }
        if (!_macFlags.HasFlag(StatusFlags.DisplaySupported))
        {
            SetDisplayStatus(T("Mac nie obsługuje ekranu wirtualnego – zaktualizuj aplikację na Macu.", "The Mac does not support the virtual display — update the Mac app."), Gray);
            return;
        }
        if (!_macFlags.HasFlag(StatusFlags.DisplayEnabled))
        {
            if (_displayRequested) StopDisplay(notifyMac: false);
            SetDisplayStatus(T("Wyłączony na Macu (Sterowanie → Ekran wirtualny dla Windowsa).", "Turned off on the Mac (Control → Virtual display for Windows)."), Gray);
            return;
        }
        if (_displayRequested) return;
        if (!OperatingSystem.IsWindows())
        {
            SetDisplayStatus(T("Podgląd ekranu Maca działa tylko na Windows.", "The Mac display viewer runs only on Windows."), Orange);
            return;
        }
        try
        {
            var monitor = DisplayNative.PickForSide(SelectedMacSide.Value);
            _displayMonitor = monitor.Bounds;
            _client.Send(Frame.DisplayStart(monitor.Bounds.Width, monitor.Bounds.Height, monitor.ScalePercent,
                InputCapture.EntryEdgeFor(SelectedMacSide.Value), mode: WantedDisplayMode));
            _displayRequested = true;
            SetDisplayStatus(T($"Uruchamianie ekranu Maca {monitor.Bounds.Width}×{monitor.Bounds.Height}…", $"Starting the {monitor.Bounds.Width}×{monitor.Bounds.Height} Mac display…"), Orange);
            Log(T($"Ekran wirtualny: prośba o {monitor.Bounds.Width}×{monitor.Bounds.Height} ({monitor.ScalePercent}%)", $"Virtual display: requested {monitor.Bounds.Width}×{monitor.Bounds.Height} ({monitor.ScalePercent}%)"));
        }
        catch (Exception ex)
        {
            SetDisplayStatus(T("Nie można odczytać monitora: ", "Cannot read the monitor: ") + ex.Message, Red);
        }
    }

    private void OnDisplayReady(DisplayReadyInfo info)
    {
        try
        {
            if (!_displayRequested) return; // spóźniona odpowiedź po wyłączeniu
            if (!info.IsOk)
            {
                StopDisplayLocal();
                SetDisplayStatus(T("Mac: ", "Mac: ") + info.Message, Red);
                Log(T("Ekran wirtualny: ", "Virtual display: ") + info.Message);
                return;
            }
            if (!OperatingSystem.IsWindows()) return;
            _viewer ??= CreateViewer();
            _viewer.Start(_displayMonitor, WantedDisplayMode == DisplayMode.Windows);
            ApplyDisplayModeLocally();
            _videoRx.Start(IPAddress.Parse(_client.RemoteAddress), info.Port, info.Key, info.Token);
            _displayRunning = true;
            _displayStatsClock.Restart();
            _displayStatsBytes = _displayStatsFrames = 0;
            SetDisplayStatus(T($"Działa · {info.Width}×{info.Height}", $"Running · {info.Width}×{info.Height}"), Green);
            Log(T($"Ekran wirtualny Maca: {info.Width}×{info.Height}, port {info.Port}", $"Mac virtual display: {info.Width}×{info.Height}, port {info.Port}"));
            UpdateViewerVisibility();
        }
        catch (Exception ex)
        {
            OnDisplayFailed(ex.Message);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(info.Key);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(info.Token);
        }
    }

    private DisplayViewer CreateViewer()
    {
        var viewer = new DisplayViewer();
        viewer.Failed += message => Post(() => OnDisplayFailed(message));
        viewer.KeyframeNeeded += streamId =>
        {
            if (_client.IsConnected) _client.Send(streamId is { } id ? Frame.DisplayKeyframe(id) : Frame.DisplayKeyframe());
        };
        viewer.WindowActivated += (id, active) => Post(() => OnMacWindowActivated(id, active));
        viewer.CloseRequested += id => { if (_client.IsConnected) _client.Send(Frame.WindowClose(id)); };
        viewer.ResizeRequested += (id, width, height) => Post(() => OnMacWindowResized(id, width, height));
        viewer.MenuInvoked += (pid, index) => { if (_client.IsConnected) _client.Send(Frame.MenuInvoke(pid, index)); };
        viewer.MenuOpening += pid => Post(() => RequestMenu(pid));
        return viewer;
    }

    /// <summary>Błąd po starcie: zatrzymujemy obie strony, bez automatycznego ponawiania.</summary>
    private void OnDisplayFailed(string message)
    {
        if (!_displayRunning) return;
        if (_client.IsConnected) _client.Send(Frame.DisplayStop());
        StopDisplayLocal();
        SetDisplayStatus(message, Red);
        Log(T("Ekran wirtualny: ", "Virtual display: ") + message);
    }

    private void StopDisplay(bool notifyMac)
    {
        if (notifyMac && _displayRequested && _client.IsConnected) _client.Send(Frame.DisplayStop());
        _displayRequested = false;
        StopDisplayLocal();
    }

    private void StopDisplayLocal()
    {
        _displayRunning = false;
        _displayFocus = false;
        _windowModeActive = false;
        _macWindows = null;
        _macWindowActive = false;
        _capture?.SetWindowMode(false, default);
        _videoRx.Stop();
        _viewer?.Stop();
        DisplayStatsText = "";
    }

    [RelayCommand]
    private void RestartDisplay()
    {
        StopDisplay(notifyMac: true);
        EvaluateDisplay();
    }

    private DisplayMode WantedDisplayMode =>
        DisplayWindowMode && _macFlags.HasFlag(StatusFlags.WindowModeSupported) ? DisplayMode.Windows : DisplayMode.Fullscreen;

    /// <summary>Tryb okien: każde okno Maca to okno Windows; tryb pełny: cały monitor.</summary>
    private void ApplyDisplayModeLocally()
    {
        _windowModeActive = WantedDisplayMode == DisplayMode.Windows;
        _macWindows = null;
        _macWindowActive = false;
        _viewer?.SetWindowMode(_windowModeActive);
        _capture?.SetWindowMode(_windowModeActive, _displayMonitor);
        UpdateViewerVisibility();
    }

    private void ApplyMacWindows(MacWindowList list)
    {
        _macWindows = list;
        var m = _displayMonitor;
        int ScaleX(int x) => m.Left + (int)((long)x * m.Width / list.DisplayWidth);
        int ScaleY(int y) => m.Top + (int)((long)y * m.Height / list.DisplayHeight);
        var windows = list.Windows.Where(w => !w.IsMenuBar).Select(w => new DisplayViewer.ProxyWindow(
            w.Id, w.Pid,
            new NativeMethods.RECT { Left = ScaleX(w.X), Top = ScaleY(w.Y), Right = ScaleX(w.X + w.Width), Bottom = ScaleY(w.Y + w.Height) },
            new NativeMethods.RECT { Left = w.X, Top = w.Y, Right = w.X + w.Width, Bottom = w.Y + w.Height },
            list.DisplayWidth, list.DisplayHeight, w.IsPopup, w.Title)).ToList();
        _viewer?.UpdateWindows(windows);
    }

    /// <summary>Punkt w oknie Windows → ten sam punkt okna Maca na ekranie wirtualnym (0…65535).</summary>
    private static (ushort x, ushort y) MapToMac(DisplayViewer.ProxyInfo proxy, NativeMethods.POINT point)
    {
        var c = proxy.Client;
        var mac = proxy.MacPixels;
        var px = mac.Left + (double)(point.X - c.Left) * mac.Width / Math.Max(1, c.Width);
        var py = mac.Top + (double)(point.Y - c.Top) * mac.Height / Math.Max(1, c.Height);
        static ushort Normalize(double value, int size) => (ushort)Math.Clamp(value * 65535 / Math.Max(1, size - 1), 0, 65535);
        return (Normalize(px, proxy.DisplayWidth), Normalize(py, proxy.DisplayHeight));
    }

    /// <summary>Ramka Windows zmieniła rozmiar okna – Mac dopasowuje okno (piksele ekranu wirtualnego).</summary>
    private void OnMacWindowResized(uint id, int width, int height)
    {
        if (!_windowModeActive || !_client.IsConnected || _macWindows is not { } list) return;
        var m = _displayMonitor;
        _client.Send(Frame.WindowResize(id, (int)((long)width * list.DisplayWidth / Math.Max(1, m.Width)),
            (int)((long)height * list.DisplayHeight / Math.Max(1, m.Height))));
    }

    private readonly Dictionary<int, long> _menuRequests = new();

    /// <summary>Świeże menu (wyszarzenia, zaznaczenia) przy aktywacji okna i otwieraniu menu.</summary>
    private void RequestMenu(int pid)
    {
        var now = Environment.TickCount64;
        if (_menuRequests.TryGetValue(pid, out var last) && now - last < 1500) return;
        _menuRequests[pid] = now;
        if (_client.IsConnected) _client.Send(Frame.MenuRequest(pid));
    }

    private void OnMacWindowActivated(uint id, bool active)
    {
        if (!_windowModeActive || !_client.IsConnected) return;
        if (active)
        {
            _client.Send(Frame.WindowRaise(id));
            _capture?.NoteRaised(id);
            if (_viewer?.Proxies.Values.FirstOrDefault(p => p.Id == id) is { Pid: > 0 } proxy) RequestMenu(proxy.Pid);
        }
        else
        {
            // Klawisze wciśnięte w chwili przełączenia (np. Alt przy Alt+Tab) nie mogą zostać na Macu.
            _client.SendReleaseAll();
        }
        _macWindowActive = active;
    }

    /// <summary>0…65535 na ekranie wirtualnym → piksele ekranu Windows (obraz wypełnia monitor).</summary>
    private NativeMethods.POINT ToScreen(ushort x, ushort y)
    {
        var m = _displayMonitor;
        return new NativeMethods.POINT
        {
            X = m.Left + (int)((long)x * Math.Max(m.Width - 1, 1) / 65535),
            Y = m.Top + (int)((long)y * Math.Max(m.Height - 1, 1) / 65535),
        };
    }

    partial void OnDisplayWindowModeChanged(bool value)
    {
        _settings.DisplayWindowMode = value;
        SaveSettings();
        if (_loading || !_displayRunning) return;
        _client.Send(Frame.SetDisplayMode(WantedDisplayMode));
        ApplyDisplayModeLocally();
    }

    /// <summary>Ekran Maca przykrywa monitor tylko podczas sterowania Makiem (tryb pełny) albo pokazuje okna Maca (tryb okien).</summary>
    private void UpdateViewerVisibility()
    {
        if (_viewer is null) return;
        if (_windowModeActive) return; // okna Maca są zwykłymi oknami Windows
        var remote = _capture?.IsRemote == true;
        _viewer.SetVisible(_displayRunning && remote && (_displayFocus || ShowMacDisplayWhileRemote));
    }

    private void UpdateWinViewStats()
    {
        if (!_winViewRunning || _winStreamer is null) return;
        var seconds = _winViewStatsClock.Elapsed.TotalSeconds;
        if (seconds < 1) return;
        var bytes = _winStreamer.BytesSent;
        var frames = _winStreamer.FramesSent;
        var fps = (frames - _winViewStatsFrames) / seconds;
        var mbps = (bytes - _winViewStatsBytes) * 8 / seconds / 1_000_000;
        _winViewStatsFrames = frames;
        _winViewStatsBytes = bytes;
        _winViewStatsClock.Restart();
        var conversion = _winStreamer.UsesGpu ? "GPU" : "CPU";
        WinViewStatsText = T($"{fps:0} kl./s · {mbps:0.0} Mb/s · konwersja {conversion}", $"{fps:0} fps · {mbps:0.0} Mb/s · {conversion} conversion");
    }

    private void UpdateDisplayStats()
    {
        if (!_displayRunning || _viewer is null) return;
        var seconds = _displayStatsClock.Elapsed.TotalSeconds;
        if (seconds < 1) return;
        var bytes = _videoRx.BytesReceived;
        var frames = _viewer.FramesPresented;
        var fps = (frames - _displayStatsFrames) / seconds;
        var mbps = (bytes - _displayStatsBytes) * 8 / seconds / 1_000_000;
        _displayStatsBytes = bytes;
        _displayStatsFrames = frames;
        _displayStatsClock.Restart();
        var decoding = _viewer.UsesGpuDecoding ? T("dekodowanie GPU", "GPU decoding") : T("dekodowanie CPU", "CPU decoding");
        DisplayStatsText = T($"{fps:0} kl./s · {mbps:0.0} Mb/s · {decoding}", $"{fps:0} fps · {mbps:0.0} Mb/s · {decoding}");
    }

    private void SetDisplayStatus(string text, IBrush brush)
    {
        DisplayStatusText = text;
        DisplayStatusBrush = brush;
    }

    // ------------------------------------------------------------------
    // Reakcje na zmiany ustawień
    // ------------------------------------------------------------------

    partial void OnHostAddressChanged(string value) { _settings.HostAddress = value.Trim(); SaveSettings(); }
    partial void OnDeviceNameChanged(string value) { _settings.DeviceName = value; SaveSettings(); }
    partial void OnAutoConnectChanged(bool value) { _settings.AutoConnect = value; SaveSettings(); }
    partial void OnHideCursorWhileRemoteChanged(bool value)
    {
        _settings.HideCursorWhileRemote = value;
        SaveSettings();
        if (_capture is not null) _capture.HideCursorWhileRemote = value;
    }

    partial void OnRemoteMouseSpeedChanged(double value)
    {
        _settings.RemoteMouseSpeed = value;
        SaveSettings();
        if (_capture is not null) _capture.RemoteMouseSpeed = value;
    }

    partial void OnControlPortTextChanged(string value)
    {
        if (int.TryParse(value, out var p) && p is > 0 and < 65536) { _settings.ControlPort = p; SaveSettings(); }
    }

    partial void OnAudioPortTextChanged(string value)
    {
        if (int.TryParse(value, out var p) && p is >= 0 and < 65536) { _settings.AudioPort = p; SaveSettings(); }
    }

    partial void OnInputSharingEnabledChanged(bool value)
    {
        _settings.InputSharingEnabled = value;
        SaveSettings();
        if (_capture is not null) _capture.Enabled = value;
        ApplyHookState();
    }

    partial void OnSelectedMacSideChanged(MacSideOption value)
    {
        _settings.MacSide = value.Value;
        SaveSettings();
        if (_capture is not null) _capture.Side = value.Value;
        // Inna strona = inny monitor i inna krawędź ekranu wirtualnego na Macu.
        if (!_loading && _displayRequested) RestartDisplay();
        if (!_loading && _winViewRequested)
        {
            StopWinView(notifyMac: true);
            EvaluateWinView();
        }
    }

    partial void OnWinViewEnabledChanged(bool value)
    {
        _settings.WinViewEnabled = value;
        SaveSettings();
        if (_loading) return;
        if (value) EvaluateWinView();
        else
        {
            StopWinView(notifyMac: true);
            SetWinViewStatus(T("Wyłączone w ustawieniach.", "Turned off in settings."), Gray);
        }
    }

    partial void OnDisplayEnabledChanged(bool value)
    {
        _settings.DisplayEnabled = value;
        SaveSettings();
        if (_loading) return;
        if (value) EvaluateDisplay();
        else
        {
            StopDisplay(notifyMac: true);
            SetDisplayStatus(T("Wyłączony w ustawieniach.", "Turned off in settings."), Gray);
        }
    }

    partial void OnShowMacDisplayWhileRemoteChanged(bool value)
    {
        _settings.ShowMacDisplayWhileRemote = value;
        SaveSettings();
        UpdateViewerVisibility();
    }

    partial void OnSelectedEmergencyHotkeyChanged(EmergencyHotkeyOption value)
    {
        _settings.EmergencyHotkey = value.Value;
        SaveSettings();
        if (_capture is not null)
        {
            _capture.EmergencyVirtualKey = value.VirtualKey;
            _capture.EmergencyRequiresModifiers = value.WithModifiers;
        }
    }

    partial void OnAudioEnabledChanged(bool value)
    {
        _settings.AudioEnabled = value;
        SaveSettings();
        if (_loading) return;
        if (value) StartAudio(); else StopAudio(notifyMac: true);
    }

    partial void OnSelectedAudioDeviceChanged(AudioDeviceInfo value)
    {
        if (value is null) return;
        _settings.AudioDeviceId = value.Id;
        SaveSettings();
        if (!_loading) RestartPlayer();
    }

    partial void OnJitterBufferMsChanged(double value)
    {
        _settings.JitterBufferMs = (int)value;
        SaveSettings();
        _jitter?.SetTarget((int)value);
    }

    private async Task ApplyClipboardAsync(ClipboardContent content)
    {
        if (_clipboardSync is not null && await _clipboardSync.ApplyAsync(content))
            ClipboardStatusText = T($"Odebrano {content.Summary} z Maca · {DateTime.Now:HH:mm:ss}", $"Received {content.Summary} from Mac · {DateTime.Now:HH:mm:ss}");
    }

    partial void OnClipboardSyncEnabledChanged(bool value)
    {
        _settings.ClipboardSyncEnabled = value;
        SaveSettings();
        if (_clipboardSync is not null) _clipboardSync.Enabled = value;
    }

    partial void OnExclusiveModeChanged(bool value)
    {
        _settings.ExclusiveMode = value;
        SaveSettings();
        if (!_loading) RestartPlayer();
    }

    partial void OnPairingCodeChanged(string value)
    {
        if (PairingCodeInvalid) PairingCodeInvalid = false;
    }

    [RelayCommand]
    private void SavePairingCode()
    {
        try
        {
            if (!PairingKeyStore.SaveCode(PairingCode))
            {
                PairingCodeInvalid = true;
                PairingStatusText = T("Kod ma nieprawidłowy format. Przepisz wszystkie grupy z Maca.", "The code format is invalid. Enter every group shown on the Mac.");
                return;
            }
            PairingCode = "";
            HasPairingKey = true;
            PairingCodeInvalid = false;
            PairingStatusText = T("Kod zapisany bezpiecznie. Możesz teraz połączyć urządzenia.", "The code is protected. You can connect the devices now.");
            Log(T("Zapisano nowy kod parowania w magazynie Windows.", "Saved a new pairing code in protected Windows storage."));
            if (!string.IsNullOrWhiteSpace(HostAddress) && !IsConnected && !IsConnecting) BeginConnect();
        }
        catch (Exception ex)
        {
            PairingCodeInvalid = true;
            PairingStatusText = T("Nie można zapisać kodu: ", "Cannot save the code: ") + ex.Message;
        }
    }

    [RelayCommand]
    private void ForgetPairing()
    {
        _desiredConnection = false;
        _connectLoopCts?.Cancel();
        _client.Disconnect(null);
        try { PairingKeyStore.Clear(); } catch (Exception ex) { Log(T("Nie udało się usunąć kodu: ", "Could not remove the pairing code: ") + ex.Message); }
        HasPairingKey = false;
        PairingCode = "";
        PairingCodeInvalid = false;
        PairingStatusText = T("Usunięto zaufanie. Wpisz aktualny kod z Maca, aby połączyć ponownie.", "Trust removed. Enter the current code from the Mac to reconnect.");
    }

    [RelayCommand]
    private void CompleteOnboarding()
    {
        HasCompletedOnboarding = true;
        _settings.HasCompletedOnboarding = true;
        SaveSettings();
    }

    private void SaveSettings()
    {
        if (_loading) return;
        _settings.Save();
    }

    // ------------------------------------------------------------------
    // Dziennik
    // ------------------------------------------------------------------

    [RelayCommand]
    private void ClearLog() => LogLines.Clear();

    private void Log(string text)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Post(() => Log(text));
            return;
        }
        LogLines.Add($"{DateTime.Now:HH:mm:ss}  {text}");
        while (LogLines.Count > 60) LogLines.RemoveAt(0);
    }
}
