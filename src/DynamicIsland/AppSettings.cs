using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace DynamicIsland;

internal enum IslandStyle
{
    /// <summary>Floating pill a few pixels below the top edge (iPhone style).</summary>
    Island,

    /// <summary>Attached to the top edge with concave shoulders (MacBook style).</summary>
    Notch,
}

internal sealed class AppSettings
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "DynamicIsland";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicIsland");

    private static string FilePath => Path.Combine(DataDirectory, "settings.json");

    public IslandStyle Style { get; set; } = IslandStyle.Island;
    public bool ShowVolume { get; set; } = true;
    public bool ShowTrackChanges { get; set; } = true;
    public bool ShowDeviceChanges { get; set; } = true;
    public bool ShowPrivacy { get; set; } = true;
    public bool ShowLyrics { get; set; } = true;
    public bool CompactLyrics { get; set; } = true;

    /// <summary>The equalizer follows the real audio (loopback) instead of a canned animation.</summary>
    public bool LiveVisualizer { get; set; } = true;

    /// <summary>Thin rim in the artwork's colour around the compact island while music plays.</summary>
    public bool AccentRim { get; set; } = true;

    /// <summary>Other apps' notifications (WhatsApp, Discord...) pop up in the island.</summary>
    public bool ShowAppNotifications { get; set; } = true;

    /// <summary>Show the message text of those notifications, or only who it is from.</summary>
    public bool ShowNotificationContent { get; set; } = true;

    /// <summary>iCal link (or .ics file) for the agenda on the "today" page; empty = none.</summary>
    public string CalendarSource { get; set; } = "";

    /// <summary>With nothing to show, shrink to a thin line at the very top so it covers nothing.</summary>
    public bool SlimWhenIdle { get; set; } = true;

    /// <summary>Reserve a strip at the top of the screen (like a taskbar) so maximised windows start below the island.</summary>
    public bool ReserveTopSpace { get; set; }

    /// <summary>Notifications shown in the island don't also pop up as Windows' own banner.</summary>
    public bool HideWindowsBanners { get; set; } = true;

    /// <summary>Apps whose Windows banner the island switched off (and turns back on when it closes).</summary>
    public List<string> BannerHiddenApps { get; set; } = [];

    /// <summary>Page with Wi-Fi, Bluetooth, microphone, sound and dark-mode switches.</summary>
    public bool ShowControlsPage { get; set; } = true;

    /// <summary>Page with CPU, memory, network and disk.</summary>
    public bool ShowSystemPage { get; set; } = true;

    /// <summary>Weather on the "today" page (Open-Meteo).</summary>
    public bool ShowWeather { get; set; } = true;

    /// <summary>City for the weather; empty = Windows' location.</summary>
    public string WeatherCity { get; set; } = "";

    /// <summary>Preview of new screenshots (Win+Shift+S, PrtSc, Win+PrtSc).</summary>
    public bool ShowScreenshots { get; set; } = true;

    /// <summary>A note when a download in the Downloads folder finishes.</summary>
    public bool ShowDownloads { get; set; } = true;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
    }

    /// <summary>Per-user autostart through the HKCU Run key (no admin rights needed).</summary>
    public static bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value)
                key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }
}

internal static class Log
{
    private static readonly bool Tracing = Environment.GetEnvironmentVariable("DYNAMICISLAND_TRACE") == "1";

    /// <summary>Step-by-step notes for diagnosing (only with DYNAMICISLAND_TRACE=1, e.g. in the smoke test).</summary>
    public static void Trace(string message)
    {
        if (!Tracing) return;
        try
        {
            Directory.CreateDirectory(AppSettings.DataDirectory);
            File.AppendAllText(Path.Combine(AppSettings.DataDirectory, "trace.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics only.
        }
    }

    public static void Error(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDirectory);
            File.AppendAllText(Path.Combine(AppSettings.DataDirectory, "error.log"), $"[{DateTime.Now:u}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Nothing sensible left to do.
        }
    }
}
