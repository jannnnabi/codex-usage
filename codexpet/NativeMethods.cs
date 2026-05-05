using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace codexpet;

internal static class NativeMethods
{
    public const int GwlExStyle = -20;
    public const int WsExTopmost = 0x00000008;
    public const int WsExToolWindow = 0x00000080;
    public const int WsExNoActivate = 0x08000000;
    public const uint MonitorDefaultToNearest = 0x00000002;
    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoZOrder = 0x0004;
    public const uint SwpNoActivate = 0x0010;

    private static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = new(-4);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr hWnd, out Rect32 rect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int index, int newLong);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromRect(ref Rect32 rect, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    public static void TryEnablePerMonitorDpi()
    {
        try
        {
            SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2);
        }
        catch
        {
        }
    }

    public static void MakeToolWindowNoActivate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var exStyle = GetWindowLong(hwnd, GwlExStyle);
        SetWindowLong(hwnd, GwlExStyle, exStyle | WsExToolWindow | WsExNoActivate);
    }

    public static string GetWindowTitle(IntPtr hwnd)
    {
        var text = new StringBuilder(256);
        GetWindowText(hwnd, text, text.Capacity);
        return text.ToString();
    }

    public static string GetWindowClass(IntPtr hwnd)
    {
        var text = new StringBuilder(256);
        GetClassName(hwnd, text, text.Capacity);
        return text.ToString();
    }

    public static Rect WorkAreaFromRect(Rect32 rect, double dpiScaleX, double dpiScaleY)
    {
        var monitorRect = rect;
        var monitor = MonitorFromRect(ref monitorRect, MonitorDefaultToNearest);
        if (monitor != IntPtr.Zero)
        {
            var info = MonitorInfo.Create();
            if (GetMonitorInfo(monitor, ref info))
            {
                return ToDipRect(info.WorkArea, dpiScaleX, dpiScaleY);
            }
        }

        return SystemParameters.WorkArea;
    }

    public static Rect WorkAreaFromDeviceRect(Rect32 rect)
    {
        var monitorRect = rect;
        var monitor = MonitorFromRect(ref monitorRect, MonitorDefaultToNearest);
        if (monitor != IntPtr.Zero)
        {
            var info = MonitorInfo.Create();
            if (GetMonitorInfo(monitor, ref info))
            {
                return ToRect(info.WorkArea);
            }
        }

        return SystemParameters.WorkArea;
    }

    public static double DpiScaleForWindow(IntPtr hwnd)
    {
        try
        {
            var dpi = GetDpiForWindow(hwnd);
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }
        catch
        {
            return 1.0;
        }
    }

    public static Rect ToDipRect(Rect32 rect, double dpiScaleX, double dpiScaleY)
    {
        return new Rect(
            rect.Left / dpiScaleX,
            rect.Top / dpiScaleY,
            Math.Max(0, rect.Width / dpiScaleX),
            Math.Max(0, rect.Height / dpiScaleY));
    }

    public static Rect ToRect(Rect32 rect)
    {
        return new Rect(
            rect.Left,
            rect.Top,
            Math.Max(0, rect.Width),
            Math.Max(0, rect.Height));
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct Rect32
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MonitorInfo
{
    public int Size;
    public Rect32 MonitorArea;
    public Rect32 WorkArea;
    public uint Flags;

    public static MonitorInfo Create()
    {
        return new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
    }
}
