using System.Windows.Threading;
using DynamicIsland.Interop;
using Microsoft.Win32;
using Windows.Devices.Radios;

namespace DynamicIsland.Services;

/// <summary>
/// The switches on the controls page: Wi-Fi and Bluetooth (Windows' radio API, the same as the
/// quick settings in the taskbar) and dark / light mode. Radios report their own changes, so
/// nothing is polled.
/// </summary>
internal sealed class QuickControls
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private readonly Dispatcher _dispatcher;
    private Radio? _wifi;
    private Radio? _bluetooth;
    private bool _started;

    public QuickControls(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>Raised on the UI thread when a radio was switched (here or anywhere else).</summary>
    public event Action? Changed;

    /// <summary>On / off, or null when this PC has no such radio (or Windows does not let us see it).</summary>
    public bool? WifiOn => State(_wifi);

    public bool? BluetoothOn => State(_bluetooth);

    /// <summary>Windows' own apps and taskbar in dark mode.</summary>
    public static bool DarkMode
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is 0;
        }
    }

    /// <summary>Finds the radios once. Off the UI thread with a time limit: the radio service may be slow or missing.</summary>
    public async void Start()
    {
        if (_started) return;
        _started = true;
        var lookup = Task.Run(async () =>
        {
            try
            {
                if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return [];
                return (IReadOnlyList<Radio>)await Radio.GetRadiosAsync();
            }
            catch (Exception)
            {
                return []; // no radio service (some desktops, servers): the switches stay greyed out
            }
        });
        if (await Task.WhenAny(lookup, Task.Delay(TimeSpan.FromSeconds(15))) != lookup) return;

        foreach (var radio in await lookup)
        {
            if (radio.Kind == RadioKind.WiFi && _wifi is null) _wifi = radio;
            else if (radio.Kind == RadioKind.Bluetooth && _bluetooth is null) _bluetooth = radio;
            else continue;
            radio.StateChanged += (_, _) => _dispatcher.InvokeAsync(() => Changed?.Invoke());
        }
        Changed?.Invoke();
    }

    public async void SetWifi(bool on) => await Set(_wifi, on);

    public async void SetBluetooth(bool on) => await Set(_bluetooth, on);

    /// <summary>Switches apps and the taskbar between dark and light, like Settings → Personalisation → Colours.</summary>
    public static void SetDarkMode(bool dark)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(PersonalizeKey);
            key.SetValue("AppsUseLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
            key.SetValue("SystemUsesLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
            Native.BroadcastSettingChange("ImmersiveColorSet"); // open windows and the taskbar repaint now
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
    }

    private static bool? State(Radio? radio) => radio is null || radio.State == RadioState.Unknown
        ? null
        : radio.State == RadioState.On;

    private async Task Set(Radio? radio, bool on)
    {
        if (radio is null) return;
        try
        {
            await radio.SetStateAsync(on ? RadioState.On : RadioState.Off);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
        Changed?.Invoke();
    }
}
