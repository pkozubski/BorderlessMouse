using System.Collections.ObjectModel;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using BorderlessMouse.Audio;
using BorderlessMouse.Input;
using BorderlessMouse.Models;
using BorderlessMouse.Net;
using BorderlessMouse.Protocol;
using BorderlessMouse.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NAudio.Wave;

namespace BorderlessMouse.ViewModels;

public sealed record MacSideOption(MacSide Value, string Label)
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
            new MacSideOption(MacSide.Left, "Po lewej"),
            new MacSideOption(MacSide.Right, "Po prawej"),
            new MacSideOption(MacSide.Top, "U góry"),
            new MacSideOption(MacSide.Bottom, "Na dole"),
        };

        _hostAddress = _settings.HostAddress;
        _controlPortText = _settings.ControlPort.ToString();
        _audioPortText = _settings.AudioPort.ToString();
        _deviceName = _settings.DeviceName;
        _autoConnect = _settings.AutoConnect;
        _inputSharingEnabled = _settings.InputSharingEnabled;
        _selectedMacSide = MacSides.First(o => o.Value == _settings.MacSide);
        _hideCursorWhileRemote = _settings.HideCursorWhileRemote;
        _remoteMouseSpeed = _settings.RemoteMouseSpeed;
        _audioEnabled = _settings.AudioEnabled;
        _jitterBufferMs = _settings.JitterBufferMs;
        _exclusiveMode = _settings.ExclusiveMode;
        _clipboardSyncEnabled = _settings.ClipboardSyncEnabled;
        _autoCheckUpdates = _settings.AutoCheckUpdates;
        _startMinimized = _settings.StartMinimized;
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
    [ObservableProperty] private string _autostartStatus = "";
    [ObservableProperty] private DiscoveredPeer? _selectedPeer;

    // ------------------------------------------------------------------
    // Stan
    // ------------------------------------------------------------------

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ConnectButtonText))] private bool _isConnected;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ConnectButtonText))] private bool _isConnecting;
    [ObservableProperty] private string _statusText = "Rozłączono";
    [ObservableProperty] private IBrush _statusBrush = Gray;
    [ObservableProperty] private string _connectionInfo = "Nie połączono.";
    [ObservableProperty] private string _macStatusText = "";
    [ObservableProperty] private string _peerName = "";
    [ObservableProperty] private double _rttMs;
    [ObservableProperty] private bool _cursorOnMac;
    [ObservableProperty] private string _cursorStatusText = "Na tym komputerze";
    [ObservableProperty] private IBrush _cursorStatusBrush = Gray;
    [ObservableProperty] private bool _audioActive;
    [ObservableProperty] private string _audioStatusText = "Nieaktywne";
    [ObservableProperty] private IBrush _audioStatusBrush = Gray;
    [ObservableProperty] private float _audioLevel;
    [ObservableProperty] private string _audioStatsText = "";
    [ObservableProperty] private bool _hasDiscoveredPeers;
    [ObservableProperty] private string _clipboardStatusText = "Brak synchronizacji w tej sesji";
    [ObservableProperty] private bool _macAccessibilityMissing;

    // aktualizacje
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private string _updateMessage = "";
    [ObservableProperty] private string _updateStatusText = "Kliknij „Sprawdź teraz”, aby sprawdzić nowe wydania na GitHubie.";
    [ObservableProperty] private bool _isCheckingUpdates;
    [ObservableProperty] private bool _isInstallingUpdate;
    [ObservableProperty] private double _updateProgress;

    public string VersionLabel => $"Wersja {Updater.CurrentVersion}";

    public string ConnectButtonText => IsConnected || IsConnecting ? "Rozłącz" : "Połącz";
    public string RemoteMouseSpeedLabel => $"{RemoteMouseSpeed:0.00}× – Raw Input nie ma akceleracji Windows, więc dostrój tempo kursora na Macu.";
    public string JitterBufferLabel => $"{(int)JitterBufferMs} ms – mniej = niższe opóźnienie, więcej = odporność na zakłócenia.";

    private static void Post(Action action) => Dispatcher.UIThread.Post(action);

    // ------------------------------------------------------------------
    // Cykl życia
    // ------------------------------------------------------------------

    public void Start()
    {
        Log("Start aplikacji");
        try
        {
            _discovery.Start();
        }
        catch (Exception ex)
        {
            Log("Discovery nie wystartowało: " + ex.Message);
        }
        _statsTimer.Start();
        if (AutoConnect && !string.IsNullOrWhiteSpace(HostAddress))
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
        UpdateStatusText = "Sprawdzanie…";
        try
        {
            var release = await _updater.CheckAsync(CancellationToken.None);
            if (release is null)
            {
                UpdateStatusText = "Brak wydań na GitHubie.";
            }
            else if (Updater.IsNewer(release.Version, Updater.CurrentVersion))
            {
                _pendingRelease = release;
                UpdateAvailable = true;
                UpdateMessage = $"Wersja {release.Version} jest gotowa do pobrania (masz {Updater.CurrentVersion}).";
                UpdateStatusText = $"Dostępna wersja {release.Version} · {release.PageUrl}";
                Log($"Dostępna aktualizacja {release.Version}");
            }
            else
            {
                UpdateStatusText = $"Masz najnowszą wersję · sprawdzono {DateTime.Now:HH:mm}";
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText = silent ? UpdateStatusText : "Nie udało się sprawdzić: " + ex.Message;
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
        UpdateStatusText = "Pobieranie aktualizacji…";
        try
        {
            var progress = new Progress<double>(p => UpdateProgress = p);
            await _updater.DownloadAndInstallAsync(_pendingRelease, progress, CancellationToken.None);
            UpdateStatusText = "Instalowanie – aplikacja uruchomi się ponownie.";
            Log("Aktualizacja pobrana, restart…");
            await Task.Delay(500);
            (Avalonia.Application.Current as App)?.ExitApplication();
        }
        catch (Exception ex)
        {
            UpdateStatusText = "Błąd aktualizacji: " + ex.Message;
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
            AutostartStatus = "Dostępne tylko na Windows.";
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
    public void LogBackgroundStart() => Log("Start w tle (autostart) – okno ukryte, ikona w zasobniku");

    /// <summary>Podpina schowek okna głównego (dostępny dopiero po utworzeniu okna).</summary>
    public void AttachClipboard(IClipboard? clipboard)
    {
        if (clipboard is null || _clipboardSync is not null) return;
        _clipboardSync = new ClipboardSync(clipboard) { Enabled = ClipboardSyncEnabled };
        _clipboardSync.LocalChanged += text =>
        {
            if (!IsConnected || !ClipboardSyncEnabled) return;
            _client.SendClipboard(text);
            ClipboardStatusText = $"Wysłano {text.Length} zn. do Maca · {DateTime.Now:HH:mm:ss}";
        };
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
        if (string.IsNullOrWhiteSpace(HostAddress))
        {
            Log("Podaj adres IP Maca lub wybierz go z listy.");
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
                Log($"Łączenie z {host}:{port}…");
                await _client.ConnectAsync(host, port, DeviceName, ct);
                await _disconnectedTcs.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log("Nie udało się połączyć: " + ex.Message);
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
        Log($"Połączono z {PeerName} ({_client.RemoteAddress})");
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
        if (was) Log(reason is null ? "Rozłączono" : "Rozłączono: " + reason);
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
                if (ClipboardSyncEnabled && _clipboardSync is not null && Frame.ParseClipboard(payload) is { Length: > 0 } text)
                {
                    _ = _clipboardSync.ApplyAsync(text);
                    ClipboardStatusText = $"Odebrano {text.Length} zn. z Maca · {DateTime.Now:HH:mm:ss}";
                }
                break;
            case MessageType.Status:
                if (payload.Length >= 1)
                {
                    var flags = (StatusFlags)payload[0];
                    var ax = flags.HasFlag(StatusFlags.AccessibilityGranted);
                    MacAccessibilityMissing = !ax;
                    MacStatusText = ax
                        ? "Mac: uprawnienie Dostępność OK"
                          + (flags.HasFlag(StatusFlags.CursorOnMac) ? " · Mac potwierdza sterowanie" : "")
                          + (flags.HasFlag(StatusFlags.AudioCapturing) ? " · przechwytuje dźwięk" : "")
                        : "Mac: BRAK uprawnienia Dostępność – nadaj je w Ustawieniach systemowych Maca (Prywatność i ochrona → Dostępność).";
                    if (!ax && !_warnedAccessibility)
                    {
                        _warnedAccessibility = true;
                        Log("Mac zgłasza brak uprawnienia Dostępność – sterowanie nie zadziała, dopóki go nie nadasz.");
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
            Log($"Znaleziono Maca: {peer.Name} ({peer.Address})");
            if (string.IsNullOrWhiteSpace(HostAddress))
            {
                HostAddress = peer.Address;
                if (AutoConnect && !_desiredConnection) BeginConnect();
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
        if (!IsConnected && !IsConnecting) BeginConnect();
    }

    private void UpdateStatus()
    {
        if (IsConnected)
        {
            StatusText = CursorOnMac ? "Sterujesz Makiem" : $"Połączono z {PeerName}";
            StatusBrush = Green;
            ConnectionInfo = $"Połączono z {PeerName} · {_client.RemoteAddress}:{_settings.ControlPort}" + (RttMs > 0 ? $" · ping {RttMs:0.0} ms" : "");
        }
        else if (IsConnecting)
        {
            StatusText = "Łączenie…";
            StatusBrush = Orange;
            ConnectionInfo = $"Próba połączenia z {HostAddress}:{_settings.ControlPort}…";
        }
        else
        {
            StatusText = "Rozłączono";
            StatusBrush = Gray;
            ConnectionInfo = "Nie połączono.";
        }
        CursorStatusText = CursorOnMac
            ? $"Na Macu · wysłane ruchy: {_capture?.RemoteMovesSent ?? 0}"
            : "Na tym komputerze";
        CursorStatusBrush = CursorOnMac ? Green : Gray;
    }

    // ------------------------------------------------------------------
    // Klawiatura i mysz
    // ------------------------------------------------------------------

    private void SetupInputCapture()
    {
        if (!OperatingSystem.IsWindows())
        {
            Log("Przechwytywanie klawiatury/myszy działa tylko na Windows.");
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
            Log("Błąd hooków: " + ex.Message);
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
            : new[] { new AudioDeviceInfo(null, "Domyślne urządzenie systemowe") };
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
            AudioStatusText = "Odtwarzanie WASAPI dostępne tylko na Windows.";
            AudioStatusBrush = Orange;
            return;
        }
        try
        {
            var port = _audioRx.Start(_settings.AudioPort);
            _audioRequested = true;
            _client.SendAudioStart((ushort)port);
            AudioStatusText = $"Poproszono Maca o strumień na port UDP {port}… (przy pierwszym użyciu Mac pokaże okno zgody na nagrywanie dźwięku)";
            AudioStatusBrush = Orange;
            Log($"Audio: nasłuch UDP na porcie {port}, wysłano AUDIO_START");
        }
        catch (Exception ex)
        {
            AudioStatusText = "Nie można otworzyć portu UDP: " + ex.Message;
            AudioStatusBrush = Red;
            Log(AudioStatusText);
        }
    }

    private void OnAudioFormat(AudioFormatInfo fmt)
    {
        if (!fmt.IsOk)
        {
            AudioStatusText = "Mac odmówił: " + fmt.Message;
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
            AudioStatusText = $"{fmt.SampleRate} Hz · {(fmt.Channels == 2 ? "stereo" : fmt.Channels + " kan.")} · {desc}";
            AudioStatusBrush = Green;
            Log("Audio gra: " + AudioStatusText);
        }
        catch (Exception ex)
        {
            AudioStatusText = "Błąd WASAPI: " + ex.Message;
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
        if (AudioActive) Log("Audio zatrzymane");
        AudioActive = false;
        AudioLevel = 0;
        AudioStatusText = "Nieaktywne";
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
            AudioStatusText = $"{_jitter.WaveFormat.SampleRate} Hz · {(_jitter.WaveFormat.Channels == 2 ? "stereo" : _jitter.WaveFormat.Channels + " kan.")} · {desc}";
            Log("Audio: zmieniono urządzenie/tryb – " + desc);
        }
        catch (Exception ex)
        {
            AudioStatusText = "Błąd WASAPI: " + ex.Message;
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
        AudioStatsText = $"bufor {_jitter.BufferedMs} ms · pakiety {_audioRx.PacketsReceived} · utracone {_audioRx.PacketsLost} · underruns {_jitter.Underruns} · przycięcia {_jitter.Overruns}";
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
