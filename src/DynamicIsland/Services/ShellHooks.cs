using System.Runtime.InteropServices;
using DynamicIsland.Interop;

namespace DynamicIsland.Services;

/// <summary>
/// Window-message based system notifications, all push (no timers):
/// <list type="bullet">
/// <item>full-screen apps, via an AppBar registration (which reserves screen space only when asked to),</item>
/// <item>AC / battery / battery-saver changes, via WM_POWERBROADCAST,</item>
/// <item>resolution, monitor and taskbar changes, via WM_DISPLAYCHANGE / WM_SETTINGCHANGE.</item>
/// </list>
/// </summary>
internal sealed class ShellHooks : IDisposable
{
    private readonly nint _hwnd;
    private readonly uint _appBarMessage = Native.RegisterWindowMessage("DynamicIsland.AppBarNotify");
    private readonly uint _taskbarCreatedMessage = Native.RegisterWindowMessage("TaskbarCreated");
    private readonly List<nint> _powerNotifications = [];
    private int _reservedHeight;
    private Native.RECT _reserved;

    public ShellHooks(nint hwnd)
    {
        _hwnd = hwnd;
        RegisterAppBar();

        foreach (var setting in new[] { Native.GUID_ACDC_POWER_SOURCE, Native.GUID_BATTERY_PERCENTAGE_REMAINING, Native.GUID_POWER_SAVING_STATUS })
        {
            var guid = setting;
            nint handle = Native.RegisterPowerSettingNotification(hwnd, ref guid, Native.DEVICE_NOTIFY_WINDOW_HANDLE);
            if (handle != 0) _powerNotifications.Add(handle);
        }
    }

    /// <summary>True when a full-screen app opened, false when it closed.</summary>
    public event Action<bool>? FullscreenChanged;

    public event Action? PowerChanged;

    public event Action? DisplayChanged;

    /// <summary>Top of the reserved strip in screen pixels, or null when nothing is reserved.</summary>
    public int? ReservedTop => _reservedHeight > 0 ? _reserved.Top : null;

    /// <summary>
    /// Reserves a strip of <paramref name="pixels"/> at the top of the primary monitor, like a second
    /// taskbar: maximised windows then start below the island instead of under it. 0 gives it back.
    /// </summary>
    public void ReserveTop(int pixels)
    {
        if (pixels == _reservedHeight && pixels == 0) return;
        _reservedHeight = Math.Max(0, pixels);
        if (_reservedHeight == 0)
        {
            // Dropping the registration is the reliable way to hand the space back; keep the notifications.
            var data = NewAppBarData();
            Native.SHAppBarMessage(Native.ABM_REMOVE, ref data);
            _reserved = default;
            RegisterAppBar();
            DisplayChanged?.Invoke();
            return;
        }
        ApplyReservation();
    }

    public nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        uint message = (uint)msg;
        if (message == _appBarMessage)
        {
            if (wParam == Native.ABN_FULLSCREENAPP) FullscreenChanged?.Invoke(lParam != 0);
            else if (wParam == Native.ABN_POSCHANGED && _reservedHeight > 0) ApplyReservation(); // e.g. the taskbar moved
        }
        else if (message == _taskbarCreatedMessage)
        {
            RegisterAppBar(); // Explorer restarted and forgot every AppBar.
            _reserved = default;
            if (_reservedHeight > 0) ApplyReservation();
        }
        else if (msg == Native.WM_POWERBROADCAST)
        {
            PowerChanged?.Invoke();
        }
        else if (msg == Native.WM_DISPLAYCHANGE || (msg == Native.WM_SETTINGCHANGE && wParam == Native.SPI_SETWORKAREA))
        {
            if (msg == Native.WM_DISPLAYCHANGE && _reservedHeight > 0) ApplyReservation(); // new resolution: new strip width
            DisplayChanged?.Invoke();
        }
        return 0;
    }

    public void Dispose()
    {
        var data = NewAppBarData();
        Native.SHAppBarMessage(Native.ABM_REMOVE, ref data);
        foreach (var handle in _powerNotifications) Native.UnregisterPowerSettingNotification(handle);
        _powerNotifications.Clear();
    }

    private void ApplyReservation()
    {
        if (!Native.TryGetPrimaryMonitor(out var monitor, out _)) return;
        var data = NewAppBarData();
        data.uEdge = Native.ABE_TOP;
        data.rc = new Native.RECT { Left = monitor.Left, Top = monitor.Top, Right = monitor.Right, Bottom = monitor.Top + _reservedHeight };
        Native.SHAppBarMessage(Native.ABM_QUERYPOS, ref data); // moves it below a taskbar docked at the top
        data.rc.Bottom = data.rc.Top + _reservedHeight;
        if (data.rc.Left == _reserved.Left && data.rc.Top == _reserved.Top && data.rc.Right == _reserved.Right && data.rc.Bottom == _reserved.Bottom)
            return; // unchanged: don't start a work-area ping-pong
        Native.SHAppBarMessage(Native.ABM_SETPOS, ref data);
        _reserved = data.rc;
        DisplayChanged?.Invoke();
    }

    private void RegisterAppBar()
    {
        // ABM_NEW without ABM_SETPOS: we only want the notifications, not a docked strip.
        var data = NewAppBarData();
        Native.SHAppBarMessage(Native.ABM_NEW, ref data);
    }

    private Native.APPBARDATA NewAppBarData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<Native.APPBARDATA>(),
        hWnd = _hwnd,
        uCallbackMessage = _appBarMessage,
    };
}
