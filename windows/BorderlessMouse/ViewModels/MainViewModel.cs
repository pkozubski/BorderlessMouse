using System.Collections.ObjectModel;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using BorderlessMouse.Audio;
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

public sealed record EmergencyHotkeyOption(EmergencyHotkey Value, ushort VirtualKey, string Label)
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
        _updateTimer = new DispatcherTimer(TimeSpan.FromHours(6), DispatcherPriority.Background,
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
                    _capture?.ReturnToLocal(leave.ratio, sendRelease: true);
                }
                break;
            case MessageType.AudioFormat:
                if (Frame.ParseAudioFormat(payload) is { } fmt) OnAudioFormat(fmt);
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
                UpdateStatus();
            };
        }
        _capture.Enabled = InputSharingEnabled;
        _capture.Side = SelectedMacSide.Value;
        _capture.EmergencyVirtualKey = SelectedEmergencyHotkey.VirtualKey;
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
    }

    partial void OnSelectedEmergencyHotkeyChanged(EmergencyHotkeyOption value)
    {
        _settings.EmergencyHotkey = value.Value;
        SaveSettings();
        if (_capture is not null) _capture.EmergencyVirtualKey = value.VirtualKey;
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
