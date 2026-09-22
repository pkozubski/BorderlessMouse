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
