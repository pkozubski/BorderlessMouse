using System.Runtime.Versioning;
using Avalonia.Threading;
using BorderlessMouse.Models;
using BorderlessMouse.Net;
using BorderlessMouse.Protocol;
using static BorderlessMouse.Input.NativeMethods;
using static BorderlessMouse.Localization.L10n;

namespace BorderlessMouse.Input;

/// <summary>
/// Maszyna stanów "kursor lokalnie / kursor na Macu". Działa na wątku UI
/// (tam wołane są hooki). W trybie zdalnym wszystkie zdarzenia są
/// blokowane lokalnie i wysyłane do Maca.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InputCapture : IDisposable
{
    private readonly LowLevelHooks _hooks = new();
    private readonly ControlClient _client;
    private NativeInputWindow? _native;
    private double _speedRemainderX, _speedRemainderY;
    private readonly HashSet<(ushort vk, ushort scan, bool ext)> _keysDown = new();
    private POINT _parked;
    private RECT _leaveMonitor;
    private bool _cursorHidden;
    private long _remoteMoves;
    private DateTime _suppressEdgeUntil = DateTime.MinValue;

    // tryb okien (wątek UI – tak jak hooki)
    private bool _windowMode;
    private RECT _macMonitor;
    private bool _windowInput;
    private int _windowButtons;
    private uint _lastRaisedWindow;
    /// <summary>Okno Maca, nad którym jest (albo które przeciąga) kursor.</summary>
    private uint _windowId;
    private DateTime _windowGraceUntil = DateTime.MinValue;

    /// <summary>Liczba ruchów myszy wysłanych do Maca w bieżącej sesji zdalnej.</summary>
    public long RemoteMovesSent => Interlocked.Read(ref _remoteMoves);
    /// <summary>Pozycja wzdłuż krawędzi przy ostatnim przejściu na Maca (0…1).</summary>
    public float LastEnterRatio { get; private set; }
    /// <summary>Czas ostatniego ENTER (UTC) – do wykrywania natychmiastowego odrzucenia przez Maca.</summary>
    public DateTime LastEnterUtc { get; private set; } = DateTime.MinValue;

    public bool Enabled { get; set; } = true;
    public MacSide Side { get; set; } = MacSide.Left;
    /// <summary>Klawisz, który zawsze ręcznie oddaje lub przejmuje sterowanie.</summary>
    public ushort EmergencyVirtualKey { get; set; } = VK_SCROLL;
    /// <summary>Skrót działa tylko z wciśniętymi Ctrl, Alt i Shift (klawiatury bez Scroll Lock).</summary>
    public bool EmergencyRequiresModifiers { get; set; }
    /// <summary>Ukrywaj kursor Windows podczas sterowania Makiem.</summary>
    public bool HideCursorWhileRemote { get; set; } = true;
    /// <summary>Mnożnik surowych delt myszy (Raw Input nie ma akceleracji Windows).</summary>
    public double RemoteMouseSpeed { get; set; } = 1.0;
    public bool IsRemote { get; private set; }
    /// <summary>true = delty z Raw Input (kursor zostaje przy krawędzi); false = tryb awaryjny z parkowaniem na środku.</summary>
    public bool UsingRawInput => _native?.IsRawInputActive == true;

    /// <summary>true = kursor przeszedł na Maca, false = wrócił. Wątek UI.</summary>
    public event Action<bool>? RemoteChanged;
    public event Action<string>? Log;

    public InputCapture(ControlClient client)
    {
        _client = client;
        _hooks.OnMouse = HandleMouse;
        _hooks.OnKeyboard = HandleKeyboard;
    }

    public bool HooksInstalled => _hooks.IsInstalled;

    public void InstallHooks()
    {
        if (_hooks.IsInstalled) return;
        _hooks.Install();
        EnsureNativeWindow();
        var raw = _native?.RegisterRawMouse() == true;
        Dispatcher.UIThread.Post(() => Log?.Invoke(raw
            ? T("Hooki aktywne, ruch myszy z Raw Input (kursor zostaje przy krawędzi)", "Hooks active with Raw Input; the pointer remains at the edge")
            : T("Hooki aktywne, Raw Input niedostępny – tryb awaryjny z parkowaniem kursora", "Hooks active; Raw Input unavailable, using pointer parking fallback")));
    }

    public void UninstallHooks()
    {
        ResetWindowInput(notifyMac: false);
        if (IsRemote) ReturnToLocal(0.5f, sendRelease: true);
        if (!_hooks.IsInstalled) return;
        _hooks.Uninstall();
        _native?.UnregisterRawMouse();
        _keysDown.Clear();
        RestoreCursor();
        Dispatcher.UIThread.Post(() => Log?.Invoke(T("Hooki klawiatury i myszy wyłączone", "Keyboard and pointer hooks disabled")));
    }

    private void EnsureNativeWindow()
    {
        if (_native is not null) return;
        try
        {
            _native = new NativeInputWindow();
            _native.RawMouseMove += OnRawMouseMove;
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => Log?.Invoke(T("Nie udało się uruchomić Raw Input: ", "Could not start Raw Input: ") + ex.Message));
        }
    }

    /// <summary>Surowa delta z WM_INPUT (wątek UI). W trybie zdalnym idzie do Maca.</summary>
    private void OnRawMouseMove(int dx, int dy)
    {
        if (!IsRemote) return;
        var speed = RemoteMouseSpeed <= 0 ? 1.0 : RemoteMouseSpeed;
        _speedRemainderX += dx * speed;
        _speedRemainderY += dy * speed;
        var sx = (int)Math.Truncate(_speedRemainderX);
        var sy = (int)Math.Truncate(_speedRemainderY);
        _speedRemainderX -= sx;
        _speedRemainderY -= sy;
        if (sx == 0 && sy == 0) return;
        _client.SendMouseMove(sx, sy);
        Interlocked.Increment(ref _remoteMoves);
    }

    /// <summary>Przełącza ręcznie wybranym klawiszem awaryjnym.</summary>
    public void Toggle()
    {
        ResetWindowInput(notifyMac: true);
        if (IsRemote) ReturnToLocal(0.5f, sendRelease: true);
        else if (_client.IsConnected && Enabled)
        {
            GetCursorPos(out var cursor);
            SwitchToRemote(EntryEdge(), 0.5f, MonitorRectAt(cursor));
        }
    }

    // ---------------- mysz ----------------

    private bool HandleMouse(int msg, in MSLLHOOKSTRUCT d)
    {
        if ((d.flags & LLMHF_INJECTED) != 0) return false;

        if (!IsRemote)
        {
            if (_windowMode && Enabled && _client.IsConnected && HandleWindowMouse(msg, d)) return true;
            if (msg == WM_MOUSEMOVE && Enabled && _client.IsConnected
                && DateTime.UtcNow >= _suppressEdgeUntil
                && TryDetectEdge(d.pt, out var ratio, out var monitor)
                && !AutomaticSwitchGuard.IsBlocked())
            {
                SwitchToRemote(EntryEdge(), ratio, monitor);
                return true;
            }
            return false;
        }

        switch (msg)
        {
            case WM_MOUSEMOVE:
            {
                // Z Raw Input delty przychodzą przez WM_INPUT – tu tylko blokujemy ruch,
                // żeby kursor Windows został tam, gdzie przekroczył krawędź.
                if (UsingRawInput) return true;
                // --- tryb awaryjny (bez Raw Input): delta z pozycji + parkowanie na środku ---
                if (d.pt.X == _parked.X && d.pt.Y == _parked.Y) return true;
                // Delta względem faktycznej pozycji kursora: działa niezależnie od tego,
                // czy system przesunął kursor mimo zablokowania zdarzenia.
                GetCursorPos(out var cur);
                var dx = d.pt.X - cur.X;
                var dy = d.pt.Y - cur.Y;
                if (dx != 0 || dy != 0)
                {
                    _client.SendMouseMove(dx, dy);
                    Interlocked.Increment(ref _remoteMoves);
                }
                if (cur.X != _parked.X || cur.Y != _parked.Y)
                {
                    // kursor zdryfował – z powrotem pod przykrywkę
                    SetCursorPos(_parked.X, _parked.Y);
                }
                return true;
            }
            case WM_LBUTTONDOWN: _client.SendMouseButton(0, true); return true;
            case WM_LBUTTONUP: _client.SendMouseButton(0, false); return true;
            case WM_RBUTTONDOWN: _client.SendMouseButton(1, true); return true;
            case WM_RBUTTONUP: _client.SendMouseButton(1, false); return true;
            case WM_MBUTTONDOWN: _client.SendMouseButton(2, true); return true;
            case WM_MBUTTONUP: _client.SendMouseButton(2, false); return true;
            case WM_XBUTTONDOWN:
            case WM_XBUTTONUP:
            {
                var which = (int)(d.mouseData >> 16) & 0xFFFF;
                _client.SendMouseButton(which == 1 ? 3 : 4, msg == WM_XBUTTONDOWN);
                return true;
            }
            case WM_MOUSEWHEEL:
                _client.SendMouseWheel(0, (short)((d.mouseData >> 16) & 0xFFFF));
                return true;
            case WM_MOUSEHWHEEL:
                _client.SendMouseWheel((short)((d.mouseData >> 16) & 0xFFFF), 0);
                return true;
            default:
                return true;
        }
    }

    // ---------------- tryb okien ----------------

    /// <summary>
    /// Tryb okien: każde okno Maca jest prawdziwym oknem Windows. Nad nim kursor Windows
    /// zostaje lokalny (bez opóźnienia obrazu), kliknięcia trafiają normalnie do tego okna
    /// (aktywacja, kolejność), a Mac dostaje ich kopię w pozycjach bezwzględnych. Klawiatura
    /// idzie do Maca, gdy aktywne jest okno Maca – jak w każdej innej aplikacji.
    /// </summary>
    public void SetWindowMode(bool enabled, RECT monitor)
    {
        if (!enabled) ResetWindowInput(notifyMac: true);
        _windowMode = enabled;
        _macMonitor = monitor;
    }

    /// <summary>
    /// Okno Maca pod punktem ekranu (tylko obszar klienta – ramkę obsługuje Windows)
    /// i ten punkt przeliczony na ekran wirtualny (0…65535).
    /// </summary>
    public Func<POINT, (uint id, ushort x, ushort y)?>? MacWindowAt { get; set; }
    /// <summary>Punkt ekranu względem wskazanego okna Maca (przeciąganie poza jego obszar).</summary>
    public Func<uint, POINT, (ushort x, ushort y)?>? MapToMacWindow { get; set; }
    /// <summary>Aktywne okno Windows jest oknem Maca (klawiatura idzie do Maca).</summary>
    public Func<bool>? MacWindowIsForeground { get; set; }

    /// <summary>
    /// Mac oddał kursor po upuszczeniu przeciąganego okna na ekranie wirtualnym:
    /// kursor Windows pojawia się w tym miejscu i od razu steruje tym oknem.
    /// </summary>
    public void HandoffToWindow(POINT point)
    {
        if (IsRemote)
        {
            IsRemote = false;
            _keysDown.Clear();
            RestoreCursor();
            RemoteChanged?.Invoke(false);
        }
        SetCursorPos(point.X, point.Y);
        // Okno Windows dla upuszczonego okna powstaje chwilę później – do tego czasu ufamy Macowi.
        _windowGraceUntil = DateTime.UtcNow.AddMilliseconds(500);
        var (x, y) = ToMac(point); // okno Windows jeszcze nie istnieje – leży tam, gdzie okno Maca
        EnterWindowInput(0, x, y);
    }

    private bool HandleWindowMouse(int msg, in MSLLHOOKSTRUCT d)
    {
        var over = MacWindowAt?.Invoke(d.pt);
        var grace = DateTime.UtcNow < _windowGraceUntil;
        if (msg == WM_MOUSEMOVE)
        {
            if (_windowInput)
            {
                // Przeciąganie trzyma kursor przy oknie Maca także poza nim (jak przechwycenie myszy).
                (ushort x, ushort y)? mapped = over is { } hit && (_windowButtons == 0 || hit.id == _windowId)
                    ? (hit.x, hit.y)
                    : _windowButtons > 0 && _windowId != 0 ? MapToMacWindow?.Invoke(_windowId, d.pt)
                    : grace ? ToMac(d.pt) : null;
                if (mapped is null)
                {
                    LeaveWindowInput();
                    return false;
                }
                if (over is { } now && _windowButtons == 0) _windowId = now.id;
                _client.Send(Frame.MouseAbsolute(mapped.Value.x, mapped.Value.y));
            }
            else if (over is { } hit)
            {
                EnterWindowInput(hit.id, hit.x, hit.y);
            }
            return false; // kursor Windows porusza się normalnie
        }

        if (!_windowInput) return false;
        if (IsButtonDown(msg) && over is { } target && target.id != _lastRaisedWindow)
        {
            // Najpierw wyciągamy okno na wierzch na Macu – inaczej kliknięcie trafiłoby
            // w okno Maca, które tam leży wyżej, choć na Windowsie jest schowane.
            _client.Send(Frame.WindowRaise(target.id));
            _lastRaisedWindow = target.id;
        }
        if (!ForwardButtonOrWheel(msg, d)) return false;
        if (IsButtonDown(msg)) _windowButtons++;
        else if (msg is WM_LBUTTONUP or WM_RBUTTONUP or WM_MBUTTONUP or WM_XBUTTONUP) _windowButtons = Math.Max(0, _windowButtons - 1);
        // Kliknięcie dociera też do okna Windows: aktywuje je i ustawia na wierzchu.
        return false;
    }

    /// <summary>Okno Maca aktywowane w Windowsie (np. Alt+Tab) – wyciągnięte na wierzch na Macu.</summary>
    public void NoteRaised(uint id) => _lastRaisedWindow = id;

    private void EnterWindowInput(uint id, ushort x, ushort y)
    {
        _windowInput = true;
        _windowButtons = 0;
        _windowId = id;
        _client.Send(Frame.WindowEnter(x, y));
    }

    private void LeaveWindowInput()
    {
        if (!_windowInput) return;
        _windowInput = false;
        _windowButtons = 0;
        _client.Send(Frame.WindowLeave(MacWindowIsForeground?.Invoke() == true));
    }

    private void ResetWindowInput(bool notifyMac)
    {
        if (notifyMac && _windowInput && _client.IsConnected)
            _client.Send(Frame.WindowLeave(keepKeyboard: false));
        _windowInput = false;
        _windowButtons = 0;
        _lastRaisedWindow = 0;
    }

    /// <summary>Skróty systemu Windows (Alt+Tab, Win, Alt+F4…) zawsze zostają w Windowsie.</summary>
    private bool IsWindowsShortcut(ushort vk)
    {
        bool Held(params ushort[] codes) => _keysDown.Any(k => codes.Contains(k.vk));
        if (vk is 0x5B or 0x5C || Held(0x5B, 0x5C)) return true;
        return vk is 0x09 or 0x1B or 0x73 && Held(0xA4, 0xA5, 0x12);
    }

    /// <summary>Punkt ekranu → 0…65535 na ekranie wirtualnym (wyświetlanym na całym monitorze).</summary>
    private (ushort x, ushort y) ToMac(POINT pt)
    {
        var m = _macMonitor;
        static ushort Scale(int value, int origin, int size) =>
            (ushort)Math.Clamp((long)(value - origin) * 65535 / Math.Max(size - 1, 1), 0, 65535);
        return (Scale(pt.X, m.Left, m.Width), Scale(pt.Y, m.Top, m.Height));
    }

    private static bool IsButtonDown(int msg) => msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN;

    /// <summary>Wysyła przycisk lub kółko do Maca; false = inne zdarzenie.</summary>
    private bool ForwardButtonOrWheel(int msg, in MSLLHOOKSTRUCT d)
    {
        switch (msg)
        {
            case WM_LBUTTONDOWN: _client.SendMouseButton(0, true); return true;
            case WM_LBUTTONUP: _client.SendMouseButton(0, false); return true;
            case WM_RBUTTONDOWN: _client.SendMouseButton(1, true); return true;
            case WM_RBUTTONUP: _client.SendMouseButton(1, false); return true;
            case WM_MBUTTONDOWN: _client.SendMouseButton(2, true); return true;
            case WM_MBUTTONUP: _client.SendMouseButton(2, false); return true;
            case WM_XBUTTONDOWN:
            case WM_XBUTTONUP:
                _client.SendMouseButton(((d.mouseData >> 16) & 0xFFFF) == 1 ? 3 : 4, msg == WM_XBUTTONDOWN);
                return true;
            case WM_MOUSEWHEEL:
                _client.SendMouseWheel(0, (short)((d.mouseData >> 16) & 0xFFFF));
                return true;
            case WM_MOUSEHWHEEL:
                _client.SendMouseWheel((short)((d.mouseData >> 16) & 0xFFFF), 0);
                return true;
            default:
                return false;
        }
    }

    private bool TryDetectEdge(POINT pt, out float ratio, out RECT monitor)
    {
        var vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        var vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        monitor = default;
        ratio = 0;
        var hit = Side switch
        {
            MacSide.Left => pt.X <= vx,
            MacSide.Right => pt.X >= vx + vw - 1,
            MacSide.Top => pt.Y <= vy,
            MacSide.Bottom => pt.Y >= vy + vh - 1,
            _ => false,
        };
        if (!hit) return false;
        monitor = MonitorRectAt(pt);
        ratio = Side is MacSide.Left or MacSide.Right
            ? (pt.Y - monitor.Top) / (float)Math.Max(monitor.Height - 1, 1)
            : (pt.X - monitor.Left) / (float)Math.Max(monitor.Width - 1, 1);
        ratio = Math.Clamp(ratio, 0f, 1f);
        return true;
    }

    /// <summary>Krawędź Maca, przez którą kursor wchodzi (przeciwna do strony, po której stoi Mac).</summary>
    private ScreenEdge EntryEdge() => EntryEdgeFor(Side);

    /// <summary>Krawędź Maca zwrócona w stronę Windowsa – tam Mac stawia też ekran wirtualny.</summary>
    public static ScreenEdge EntryEdgeFor(MacSide side) => side switch
    {
        MacSide.Left => ScreenEdge.Right,
        MacSide.Right => ScreenEdge.Left,
        MacSide.Top => ScreenEdge.Bottom,
        MacSide.Bottom => ScreenEdge.Top,
        _ => ScreenEdge.Right,
    };

    private void SwitchToRemote(ScreenEdge entryEdge, float ratio, RECT monitor)
    {
        // Ta metoda działa wewnątrz hooka niskiego poziomu – musi być szybka.
        // Wszystko, co dotyka UI (okno-przykrywka, dziennik), idzie przez Dispatcher.Post.
        _leaveMonitor = monitor;
        // Przeciągane okno Maca jedzie przez krawędź na ekran Maca: Mac zachowuje wciśnięty
        // przycisk, więc nie wysyłamy WINDOW_LEAVE (ono zwolniłoby przycisk).
        _windowInput = false;
        _windowButtons = 0;
        IsRemote = true;
        Interlocked.Exchange(ref _remoteMoves, 0);
        LastEnterUtc = DateTime.UtcNow;
        LastEnterRatio = ratio;
        ReleaseLocalKeys();
        _speedRemainderX = _speedRemainderY = 0;
        _client.SendEnter(entryEdge, ratio);
        if (UsingRawInput)
        {
            // kursor zostaje tam, gdzie jest (przy krawędzi) – hook blokuje dalsze ruchy
            GetCursorPos(out _parked);
        }
        else
        {
            // tryb awaryjny: delty liczone z pozycji, więc kursor musi stać daleko od krawędzi
            _parked = new POINT
            {
                X = GetSystemMetrics(SM_CXSCREEN) / 2,
                Y = GetSystemMetrics(SM_CYSCREEN) / 2,
            };
            SetCursorPos(_parked.X, _parked.Y);
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsRemote) return;
            RemoteChanged?.Invoke(true);
            if (HideCursorWhileRemote) HideCursor();
            Log?.Invoke(T(
                $"Kursor przeszedł na Maca (krawędź {Side}, pozycja {ratio:0.00}, {(UsingRawInput ? "Raw Input" : "tryb awaryjny")})",
                $"Pointer moved to Mac (edge {Side}, position {ratio:0.00}, {(UsingRawInput ? "Raw Input" : "fallback")})"));
        });
    }

    private void HideCursor()
    {
        if (_cursorHidden) return;
        _cursorHidden = true;
        // okno z pustym kursorem klasy pod kursorem + licznik ShowCursor dla naszego wątku
        _native?.ShowHiderAt(_parked.X, _parked.Y);
        ShowCursor(false);
        // wstrzyknięty ruch (przechodzi przez hook) odświeża obraz kursora nad hiderem
        SetCursorPos(_parked.X, _parked.Y);
    }

    private void RestoreCursor()
    {
        if (!_cursorHidden) return;
        _cursorHidden = false;
        ShowCursor(true);
        _native?.HideHider();
    }

    /// <summary>
    /// Wraca do sterowania lokalnego. ratio = pozycja wzdłuż krawędzi (z LEAVE Maca).
    /// <paramref name="farEdgeOf"/>: kursor wyszedł z ekranu wirtualnego pokazanego na tym
    /// monitorze – pojawia się przy jego drugiej krawędzi, tam gdzie był na obrazie.
    /// </summary>
    public void ReturnToLocal(float ratio, bool sendRelease, RECT? farEdgeOf = null)
    {
        if (!IsRemote) return;
        IsRemote = false;
        _keysDown.Clear();
        if (sendRelease && _client.IsConnected) _client.SendReleaseAll();

        // Natychmiastowy powrót (< 400 ms) = Mac odrzucił sterowanie. Odsuwamy kursor
        // dalej od krawędzi i blokujemy ponowne wejście na sekundę, żeby nie odbijał
        // się w pętli krawędź ↔ środek.
        var rejected = DateTime.UtcNow - LastEnterUtc < TimeSpan.FromMilliseconds(400);
        var inset = rejected ? 40 : 3;
        if (rejected) _suppressEdgeUntil = DateTime.UtcNow.AddSeconds(1);

        var m = _leaveMonitor;
        if (m.Width <= 0 || m.Height <= 0) m = MonitorRectAt(_parked);
        ratio = Math.Clamp(ratio, 0f, 1f);
        var vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        var vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        var (x, y) = farEdgeOf is { Width: > 0, Height: > 0 } far ? Side switch
        {
            MacSide.Left => (far.Right - 1 - inset, far.Top + (int)(ratio * (far.Height - 1))),
            MacSide.Right => (far.Left + inset, far.Top + (int)(ratio * (far.Height - 1))),
            MacSide.Top => (far.Left + (int)(ratio * (far.Width - 1)), far.Bottom - 1 - inset),
            MacSide.Bottom => (far.Left + (int)(ratio * (far.Width - 1)), far.Top + inset),
            _ => (_parked.X, _parked.Y),
        } : Side switch
        {
            MacSide.Left => (vx + inset, m.Top + (int)(ratio * (m.Height - 1))),
            MacSide.Right => (vx + vw - 1 - inset, m.Top + (int)(ratio * (m.Height - 1))),
            MacSide.Top => (m.Left + (int)(ratio * (m.Width - 1)), vy + inset),
            MacSide.Bottom => (m.Left + (int)(ratio * (m.Width - 1)), vy + vh - 1 - inset),
            _ => (_parked.X, _parked.Y),
        };
        var moves = RemoteMovesSent;
        Dispatcher.UIThread.Post(() =>
        {
            RestoreCursor();
            RemoteChanged?.Invoke(false);
            SetCursorPos(x, y);
            Log?.Invoke(rejected
                ? T("Mac natychmiast oddał sterowanie – sprawdź uprawnienie Dostępność i przełącznik przyjmowania klawiatury i myszy.", "Mac returned control immediately. Check Accessibility permission and the keyboard and mouse control toggle on the Mac.")
                : T($"Kursor wrócił na Windows (wysłano {moves} ruchów myszy)", $"Pointer returned to Windows ({moves} pointer events sent)"));
        });
    }

    // ---------------- klawiatura ----------------

    private bool HandleKeyboard(int msg, in KBDLLHOOKSTRUCT d)
    {
        if ((d.flags & LLKHF_INJECTED) != 0) return false;
        var down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
        var vk = (ushort)d.vkCode;
        var scan = (ushort)d.scanCode;
        var ext = (d.flags & LLKHF_EXTENDED) != 0;

        if (vk == EmergencyVirtualKey && Enabled && _client.IsConnected
            && (!EmergencyRequiresModifiers || EmergencyModifiersHeld()))
        {
            if (down) Toggle();
            return true;
        }

        var key = (vk, scan, ext);
        var repeat = down && _keysDown.Contains(key);
        if (down) _keysDown.Add(key); else _keysDown.Remove(key);

        if (!IsRemote)
        {
            // Tryb okien: klawiatura należy do aktywnego okna – Maca albo Windows.
            if (!_windowMode || !_client.IsConnected || MacWindowIsForeground?.Invoke() != true || IsWindowsShortcut(vk)) return false;
            _client.SendKey(scan, vk, ext, down, repeat);
            // Ctrl/Alt/Shift widzi też Windows – inaczej Alt+Tab i Alt+F4 nie zadziałałyby.
            return vk is not (0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5);
        }
        _client.SendKey(scan, vk, ext, down, repeat);
        return true;
    }

    /// <summary>Ctrl, Alt i Shift wciśnięte (lewe lub prawe) – według stanu śledzonego przez hook.</summary>
    private bool EmergencyModifiersHeld()
    {
        bool Held(params ushort[] codes) => _keysDown.Any(k => codes.Contains(k.vk));
        return Held(0xA2, 0xA3, 0x11) && Held(0xA4, 0xA5, 0x12) && Held(0xA0, 0xA1, 0x10);
    }

    /// <summary>
    /// Klawisze trzymane w chwili przejścia na Maca dostałyby "key up" tylko
    /// na Macu i zostałyby "zawieszone" w Windowsie – zwalniamy je lokalnie.
    /// </summary>
    private void ReleaseLocalKeys()
    {
        foreach (var (vk, scan, ext) in _keysDown)
        {
            keybd_event((byte)vk, (byte)scan, KEYEVENTF_KEYUP | (ext ? KEYEVENTF_EXTENDEDKEY : 0), UIntPtr.Zero);
        }
        _keysDown.Clear();
    }

    public void Dispose()
    {
        UninstallHooks();
        _hooks.Dispose();
        _native?.Dispose();
        _native = null;
    }
}
