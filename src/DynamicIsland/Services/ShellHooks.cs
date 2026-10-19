using System.Runtime.InteropServices;
using DynamicIsland.Interop;

namespace DynamicIsland.Services;

/// <summary>
/// Window-message based system notifications, all push (no timers):
/// <list type="bullet">
/// <item>full-screen apps, via an AppBar registration that reserves no screen space,</item>
/// <item>AC / battery changes, via WM_POWERBROADCAST,</item>
/// <item>resolution, monitor and taskbar changes, via WM_DISPLAYCHANGE / WM_SETTINGCHANGE.</item>
/// </list>
/// </summary>
internal sealed class ShellHooks : IDisposable
{
    private readonly nint _hwnd;
    private readonly uint _appBarMessage = Native.RegisterWindowMessage("DynamicIsland.AppBarNotify");
    private readonly uint _taskbarCreatedMessage = Native.RegisterWindowMessage("TaskbarCreated");
    private readonly List<nint> _powerNotifications = [];

    public ShellHooks(nint hwnd)
    {
        _hwnd = hwnd;
        RegisterAppBar();

        foreach (var setting in new[] { Native.GUID_ACDC_POWER_SOURCE, Native.GUID_BATTERY_PERCENTAGE_REMAINING })
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

    public nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        uint message = (uint)msg;
        if (message == _appBarMessage)
        {
            if (wParam == Native.ABN_FULLSCREENAPP) FullscreenChanged?.Invoke(lParam != 0);
        }
        else if (message == _taskbarCreatedMessage)
        {
            RegisterAppBar(); // Explorer restarted and forgot every AppBar.
        }
        else if (msg == Native.WM_POWERBROADCAST)
        {
            PowerChanged?.Invoke();
        }
        else if (msg == Native.WM_DISPLAYCHANGE || (msg == Native.WM_SETTINGCHANGE && wParam == Native.SPI_SETWORKAREA))
        {
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
