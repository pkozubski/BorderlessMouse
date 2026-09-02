using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using static BorderlessMouse.Input.NativeMethods;

namespace BorderlessMouse.Input;

/// <summary>
/// Chroni lokalne sterowanie, gdy aplikacja używa pełnego ekranu lub przejęła mysz.
/// Dotyczy tylko automatycznego przejścia przez krawędź; Scroll Lock jest świadomym wyjątkiem.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class AutomaticSwitchGuard
{
    public static bool IsBlocked()
    {
        // Sprawdzamy bieżący stan dopiero przy próbie przejścia przez krawędź.
        // Bez cache: Alt+Tab oraz zmiana trybu okna muszą zadziałać od razu.
        var cursor = new CURSORINFO { cbSize = (uint)Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref cursor)
            || (cursor.flags & CURSOR_SHOWING) == 0
            || cursor.hCursor == IntPtr.Zero)
            return true;

        if (!GetClipCursor(out var clip)) return true;
        var desktop = new RECT
        {
            Left = GetSystemMetrics(SM_XVIRTUALSCREEN),
            Top = GetSystemMetrics(SM_YVIRTUALSCREEN),
        };
        desktop.Right = desktop.Left + GetSystemMetrics(SM_CXVIRTUALSCREEN);
        desktop.Bottom = desktop.Top + GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (IsCursorConfined(clip, desktop)) return true;

        var foreground = GetForegroundWindow();
        // Brak aktywnego okna bywa przejściowy podczas zmiany fokusu. Zostawiamy wejście lokalnie.
        if (foreground == IntPtr.Zero) return true;
        if (foreground == GetDesktopWindow() || foreground == GetShellWindow()) return false;

        if (!GetClientRect(foreground, out var client)) return true;
        var topLeft = new POINT { X = client.Left, Y = client.Top };
        var bottomRight = new POINT { X = client.Right, Y = client.Bottom };
        if (!ClientToScreen(foreground, ref topLeft) || !ClientToScreen(foreground, ref bottomRight))
            return true;
        client = new RECT { Left = topLeft.X, Top = topLeft.Y, Right = bottomRight.X, Bottom = bottomRight.Y };

        // Monitor aktywnego okna, a nie główny ekran ani monitor pod kursorem.
        var monitor = MonitorFromWindow(foreground, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return true;
        if (!CoversMonitor(client, info.rcMonitor)) return false;

        // Pulpit Explorera może być osobnym WorkerW, innym niż GetShellWindow().
        var className = new StringBuilder(64);
        GetClassName(foreground, className, className.Capacity);
        return className.ToString() is not ("Progman" or "WorkerW");
    }

    internal static bool IsCursorConfined(RECT clip, RECT desktop) =>
        clip.Left > desktop.Left || clip.Top > desktop.Top
        || clip.Right < desktop.Right || clip.Bottom < desktop.Bottom;

    internal static bool CoversMonitor(RECT client, RECT monitor) =>
        client.Width > 0 && client.Height > 0 && monitor.Width > 0 && monitor.Height > 0
        && client.Left <= monitor.Left && client.Top <= monitor.Top
        && client.Right >= monitor.Right && client.Bottom >= monitor.Bottom;
}
