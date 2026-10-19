using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DynamicIsland.Interop;

internal static class Native
{
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x00000080;   // hidden from Alt+Tab
    public const long WS_EX_NOACTIVATE = 0x08000000;   // clicking never steals focus

    public static readonly nint HWND_TOPMOST = -1;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;

    public const int WM_SETTINGCHANGE = 0x001A;
    public const int SPI_SETWORKAREA = 0x002F;
    public const int WM_DISPLAYCHANGE = 0x007E;
    public const int WM_POWERBROADCAST = 0x0218;
    public const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0;

    public const uint ABM_NEW = 0x0;
    public const uint ABM_REMOVE = 0x1;
    public const int ABN_FULLSCREENAPP = 0x2;

    public const uint MONITOR_DEFAULTTONULL = 0;
    public const uint MONITOR_DEFAULTTOPRIMARY = 1;

    public static readonly Guid GUID_ACDC_POWER_SOURCE = new("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548");
    public static readonly Guid GUID_BATTERY_PERCENTAGE_REMAINING = new("A7AD8041-B45A-4CAE-87A3-EECBB468A9E1");
    public static readonly Guid GUID_POWER_SAVING_STATUS = new("E00958C0-C213-4ACE-AC77-FECCED2EEEA5");

    public const uint REG_NOTIFY_CHANGE_NAME = 0x00000001;
    public const uint REG_NOTIFY_CHANGE_LAST_SET = 0x00000004;
    public const uint REG_NOTIFY_THREAD_AGNOSTIC = 0x10000000; // registration survives the calling thread

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA
    {
        public uint cbSize;
        public nint hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public nint lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr64(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr64(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(nint hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(nint hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern nint MonitorFromPoint(POINT pt, uint flags);

    [DllImport("user32.dll")]
    public static extern nint MonitorFromWindow(nint hWnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern nint GetShellWindow();

    [DllImport("user32.dll")]
    public static extern nint GetDesktopWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessage(string message);

    [DllImport("shell32.dll")]
    public static extern nuint SHAppBarMessage(uint message, ref APPBARDATA data);

    [DllImport("user32.dll")]
    public static extern nint RegisterPowerSettingNotification(nint hRecipient, ref Guid powerSettingGuid, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterPowerSettingNotification(nint handle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [DllImport("advapi32.dll")]
    public static extern int RegNotifyChangeKeyValue(
        SafeRegistryHandle hKey,
        [MarshalAs(UnmanagedType.Bool)] bool watchSubtree,
        uint notifyFilter,
        SafeWaitHandle hEvent,
        [MarshalAs(UnmanagedType.Bool)] bool asynchronous);

    public static void AddExStyle(nint hwnd, long flags)
    {
        if (IntPtr.Size == 8)
            SetWindowLongPtr64(hwnd, GWL_EXSTYLE, GetWindowLongPtr64(hwnd, GWL_EXSTYLE) | (nint)flags);
        else
            SetWindowLong32(hwnd, GWL_EXSTYLE, GetWindowLong32(hwnd, GWL_EXSTYLE) | (int)flags);
    }

    public static void BringToTopmost(nint hwnd) =>
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    /// <summary>Full bounds and work area (screen minus taskbar) of the primary monitor, in pixels.</summary>
    public static bool TryGetPrimaryMonitor(out RECT bounds, out RECT workArea)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        bool ok = GetMonitorInfo(MonitorFromPoint(default, MONITOR_DEFAULTTOPRIMARY), ref info);
        bounds = info.rcMonitor;
        workArea = info.rcWork;
        return ok;
    }

    /// <summary>
    /// True when the foreground window covers the whole monitor that hosts <paramref name="self"/>
    /// (games, F11 browsers, video players, slideshows). The desktop itself is excluded because the
    /// shell also reports it as "full screen" on some Windows builds.
    /// </summary>
    public static bool IsForegroundFullscreen(nint self)
    {
        nint foreground = GetForegroundWindow();
        if (foreground == 0 || foreground == self || foreground == GetShellWindow() || foreground == GetDesktopWindow())
            return false;

        var className = new StringBuilder(64);
        GetClassName(foreground, className, className.Capacity);
        if (className.ToString() is "Progman" or "WorkerW")
            return false;

        nint monitor = MonitorFromWindow(foreground, MONITOR_DEFAULTTONULL);
        if (monitor == 0 || monitor != MonitorFromWindow(self, MONITOR_DEFAULTTOPRIMARY))
            return false;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info) || !GetWindowRect(foreground, out var rect))
            return false;

        var m = info.rcMonitor;
        return rect.Left <= m.Left && rect.Top <= m.Top && rect.Right >= m.Right && rect.Bottom >= m.Bottom;
    }
}
