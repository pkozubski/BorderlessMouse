using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static BorderlessMouse.Input.NativeMethods;

namespace BorderlessMouse.Display;

/// <summary>Win32: pętla komunikatów okna podglądu i wybór monitora.</summary>
[SupportedOSPlatform("windows")]
internal static class DisplayNative
{
    public const uint WM_SETCURSOR = 0x0020;
    public const uint WM_MOUSEACTIVATE = 0x0021;
    public const uint WM_CLOSE = 0x0010;
    public const int MA_NOACTIVATE = 3;
    public const int SW_SHOWNOACTIVATE = 4;
    public const uint PM_REMOVE = 0x0001;
    public const uint QS_ALLINPUT = 0x04FF;
    public const uint MWMO_INPUTAVAILABLE = 0x0004;
    public const uint MONITORINFOF_PRIMARY = 0x00000001;
    public const int MDT_EFFECTIVE_DPI = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    public static extern uint MsgWaitForMultipleObjectsEx(uint nCount, IntPtr[] pHandles, uint dwMilliseconds, uint dwWakeMask, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PeekMessageW(out MSG msg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    public const int IDC_ARROW = 32512;

    [DllImport("user32.dll")]
    public static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr cursorName);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr handle);

    /// <summary>PNG jako zasób ikony (Windows Vista+).</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr CreateIconFromResourceEx(byte[] data, uint size, [MarshalAs(UnmanagedType.Bool)] bool icon,
        uint version, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowTextW(IntPtr hWnd, string text);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    public const uint GA_ROOT = 2;

    public const uint MF_STRING = 0x0000;
    public const uint MF_GRAYED = 0x0001;
    public const uint MF_CHECKED = 0x0008;
    public const uint MF_POPUP = 0x0010;
    public const uint MF_SEPARATOR = 0x0800;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AdjustWindowRectEx(ref RECT rect, uint style, [MarshalAs(UnmanagedType.Bool)] bool menu, uint exStyle);

    [DllImport("user32.dll")]
    public static extern IntPtr CreateMenu();

    [DllImport("user32.dll")]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr idOrSubmenu, string? text);

    /// <summary>Menu okna (<c>SetMenu</c> z user32 – nazwa zmieniona, żeby nie kolidować z metodą podglądu).</summary>
    [DllImport("user32.dll", EntryPoint = "SetMenu")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowMenu(IntPtr hWnd, IntPtr menu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DrawMenuBar(IntPtr hWnd);

    /// <summary>Po udanym wywołaniu system przejmuje uchwyt regionu.</summary>
    [DllImport("user32.dll")]
    public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    public readonly record struct MonitorArea(RECT Bounds, int Dpi, bool Primary)
    {
        public int ScalePercent => (int)Math.Round(Dpi * 100.0 / 96.0);
    }

    /// <summary>Monitor Windows najbliżej Maca (skrajny po stronie Maca; przy remisie główny).</summary>
    public static MonitorArea PickForSide(Models.MacSide side)
    {
        var monitors = Monitors();
        if (monitors.Count == 0) throw new InvalidOperationException("No monitors");
        Func<MonitorArea, int> edge = side switch
        {
            Models.MacSide.Left => m => -m.Bounds.Left,
            Models.MacSide.Right => m => m.Bounds.Right,
            Models.MacSide.Top => m => -m.Bounds.Top,
            _ => m => m.Bounds.Bottom,
        };
        return monitors.OrderByDescending(edge).ThenByDescending(m => m.Primary).First();
    }

    public static List<MonitorArea> Monitors()
    {
        var list = new List<MonitorArea>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref info)) return true;
            var dpi = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 ? (int)dpiX : 96;
            list.Add(new MonitorArea(info.rcMonitor, dpi, (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
