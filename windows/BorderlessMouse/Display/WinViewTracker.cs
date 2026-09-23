using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using BorderlessMouse.Input;
using BorderlessMouse.Protocol;

namespace BorderlessMouse.Display;

/// <summary>
/// Śledzi okna leżące na wirtualnym monitorze (lista dla Maca, ~30 razy na sekundę, tylko po
/// zmianie) i kursor Windows na tym monitorze (pozycja i kształt, ~60 razy na sekundę).
/// Działa na własnym wątku; funkcja wysyłająca musi być bezpieczna wątkowo.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WinViewTracker : IDisposable
{
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
        "ApplicationFrameWindow_Hidden", "IME", "MSCTFIME UI",
    };

    private readonly Action<byte[]> _send;
    private readonly NativeMethods.RECT _monitor;
    private readonly int _ownProcess = Environment.ProcessId;
    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _resend;
    /// <summary>Prostokąty okien (współrzędne ekranu) z ostatnio wysłanej listy – do decyzji kursora.</summary>
    private volatile NativeMethods.RECT[] _rects = [];
    private IReadOnlyList<WinWindow> _lastWindows = [];
    private (int x, int y, byte shape, bool visible)? _lastCursor;
    private readonly Dictionary<IntPtr, byte> _cursorShapes = new();

    public WinViewTracker(NativeMethods.RECT monitor, Action<byte[]> send)
    {
        _monitor = monitor;
        _send = send;
        // Kursory systemowe są współdzielone – porównujemy uchwyty (numeracja jak CURSOR_SHAPE).
        (int id, byte shape)[] cursors =
        [
            (32512, 0), (32513, 1), (32649, 2), (32644, 3), (32645, 4), (32515, 5), (32648, 6),
            (32646, 8), (32642, 9), (32643, 10), (32514, 11), (32650, 0),
        ];
        foreach (var (id, shape) in cursors)
        {
            var handle = LoadCursorW(IntPtr.Zero, (IntPtr)id);
            if (handle != IntPtr.Zero) _cursorShapes[handle] = shape;
        }
    }

    public NativeMethods.RECT Monitor => _monitor;

    /// <summary>Czy punkt ekranu leży w którymś oknie widocznym na Macu.</summary>
    public bool WindowAt(NativeMethods.POINT point)
    {
        foreach (var r in _rects)
        {
            if (point.X >= r.Left && point.X < r.Right && point.Y >= r.Top && point.Y < r.Bottom) return true;
        }
        return false;
    }

    /// <summary>Punkt ekranu → piksel wirtualnego monitora.</summary>
    public (int x, int y) ToMonitor(NativeMethods.POINT point) => (point.X - _monitor.Left, point.Y - _monitor.Top);

    public bool Contains(NativeMethods.POINT point) =>
        point.X >= _monitor.Left && point.X < _monitor.Right && point.Y >= _monitor.Top && point.Y < _monitor.Bottom;

    /// <summary>Mac prosi o pełny stan (np. po klatce kluczowej).</summary>
    public void Resend() => _resend = true;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _resend = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "blm-winview-tracker" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        var thread = _thread;
        if (thread is not null && thread != Thread.CurrentThread) thread.Join(TimeSpan.FromSeconds(1));
        _thread = null;
    }

    public void Dispose() => Stop();

    private void Run()
    {
        var tick = 0;
        while (_running)
        {
            try
            {
                if (tick++ % 2 == 0) PollWindows();
                PollCursor();
            }
            catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
            {
                // pojedynczy nieudany odczyt nie przerywa śledzenia
            }
            Thread.Sleep(16);
        }
    }

    private void PollWindows()
    {
        var windows = Snapshot();
        var resend = _resend;
        _resend = false;
        if (!resend && windows.SequenceEqual(_lastWindows)) return;
        _lastWindows = windows;
        _rects = windows.Select(w => new NativeMethods.RECT
        {
            Left = _monitor.Left + w.X, Top = _monitor.Top + w.Y,
            Right = _monitor.Left + w.X + w.Width, Bottom = _monitor.Top + w.Y + w.Height,
        }).ToArray();
        _send(Frame.WinViewWindows(_monitor.Width, _monitor.Height, windows));
    }

    private List<WinWindow> Snapshot()
    {
        var list = new List<WinWindow>();
        var foreground = GetForegroundWindow();
        var className = new StringBuilder(128);
        var title = new StringBuilder(256);
        EnumWindows((hwnd, _) =>
        {
            if (list.Count >= 64) return false;
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == _ownProcess) return true;
            className.Clear();
            GetClassNameW(hwnd, className, className.Capacity);
            if (IgnoredClasses.Contains(className.ToString())) return true;
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out NativeMethods.RECT bounds, Marshal.SizeOf<NativeMethods.RECT>()) != 0
                && !GetWindowRect(hwnd, out bounds)) return true;
            if (bounds.Width < 4 || bounds.Height < 4) return true;
            if (bounds.Right <= _monitor.Left || bounds.Left >= _monitor.Right || bounds.Bottom <= _monitor.Top || bounds.Top >= _monitor.Bottom) return true;
            var style = (long)GetWindowLongPtrW(hwnd, GWL_STYLE);
            var exStyle = (long)GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
            // Menu, podpowiedzi, listy rozwijane: na Macu zawsze nad oknami, bez cienia.
            var cls = className.ToString();
            var popup = cls == "#32768" || cls.Contains("tooltips", StringComparison.OrdinalIgnoreCase)
                        || ((exStyle & WS_EX_TOOLWINDOW) != 0 && (exStyle & WS_EX_APPWINDOW) == 0)
                        || ((style & WS_POPUP) != 0 && (style & (WS_CAPTION | WS_THICKFRAME)) == 0 && GetWindow(hwnd, GW_OWNER) != IntPtr.Zero);
            byte flags = 0;
            if (hwnd == foreground) flags |= WinWindow.Foreground;
            if (popup) flags |= WinWindow.Popup;
            title.Clear();
            GetWindowTextW(hwnd, title, title.Capacity);
            list.Add(new WinWindow(unchecked((uint)(long)hwnd), bounds.Left - _monitor.Left, bounds.Top - _monitor.Top,
                bounds.Width, bounds.Height, flags, title.ToString()));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private void PollCursor()
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref info)) return;
        var point = info.ptScreenPos;
        if (!Contains(point))
        {
            _lastCursor = null;
            return;
        }
        var shape = _cursorShapes.TryGetValue(info.hCursor, out var known) ? known : (byte)0;
        var current = (point.X - _monitor.Left, point.Y - _monitor.Top, shape, (info.flags & CURSOR_SHOWING) != 0);
        if (_lastCursor == current) return;
        _lastCursor = current;
        _send(Frame.WinViewCursor(current.Item1, current.Item2, current.shape, current.Item4));
    }

    // ---------------- Win32 ----------------

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const long WS_POPUP = 0x80000000L;
    private const long WS_CAPTION = 0x00C00000L;
    private const long WS_THICKFRAME = 0x00040000L;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const long WS_EX_APPWINDOW = 0x00040000L;
    private const uint GW_OWNER = 4;
    private const int CURSOR_SHOWING = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public NativeMethods.POINT ptScreenPos;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int capacity);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int capacity);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeMethods.RECT rect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeMethods.RECT value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorInfo(ref CURSORINFO info);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursorW(IntPtr instance, IntPtr name);
}
