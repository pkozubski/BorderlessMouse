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

    /// <summary>Liczba ruchów myszy wysłanych do Maca w bieżącej sesji zdalnej.</summary>
    public long RemoteMovesSent => Interlocked.Read(ref _remoteMoves);
    /// <summary>Czas ostatniego ENTER (UTC) – do wykrywania natychmiastowego odrzucenia przez Maca.</summary>
    public DateTime LastEnterUtc { get; private set; } = DateTime.MinValue;

    public bool Enabled { get; set; } = true;
    public MacSide Side { get; set; } = MacSide.Left;
    /// <summary>Klawisz, który zawsze ręcznie oddaje lub przejmuje sterowanie.</summary>
    public ushort EmergencyVirtualKey { get; set; } = VK_SCROLL;
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
    private ScreenEdge EntryEdge() => Side switch
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
        IsRemote = true;
        Interlocked.Exchange(ref _remoteMoves, 0);
        LastEnterUtc = DateTime.UtcNow;
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

    /// <summary>Wraca do sterowania lokalnego. ratio = pozycja wzdłuż krawędzi (z LEAVE Maca).</summary>
    public void ReturnToLocal(float ratio, bool sendRelease)
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
        var (x, y) = Side switch
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

        if (vk == EmergencyVirtualKey && Enabled && _client.IsConnected)
        {
            if (down) Toggle();
            return true;
        }

        var key = (vk, scan, ext);
        var repeat = down && _keysDown.Contains(key);
        if (down) _keysDown.Add(key); else _keysDown.Remove(key);

        if (!IsRemote) return false;
        _client.SendKey(scan, vk, ext, down, repeat);
        return true;
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
